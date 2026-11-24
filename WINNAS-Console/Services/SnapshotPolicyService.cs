using System.Diagnostics;
using System.Text;
using Winnas.Console.Infrastructure;
using Winnas.Console.Models;

namespace Winnas.Console.Services;

/// <summary>
/// Turns "make me a snapshot every day at 03:00 and keep five" into something
/// that actually happens without the console being open.
///
/// The trick is that the EXE cannot stay resident, so policy enforcement is a
/// scheduled task that runs "WINNAS.Console.exe --snapshot" hourly. The task is
/// dumb on purpose — it fires every hour and this class decides what is due,
/// which keeps every schedule (hourly/daily/weekly) working off one trigger.
/// </summary>
public static class SnapshotPolicyService
{
    public const string TaskName = "WINNAS Snapshot Policy";

    // ------------------------------------------------------------ due logic

    public static bool IsDue(SnapshotPolicy p, DateTime now)
    {
        if (!p.Enabled) return false;

        if (p.LastRun is not null)
        {
            var last = p.LastRun.Value;
            var since = now - last;
            // A run in the same scheduling bucket already happened.
            var alreadyDone = p.Frequency switch
            {
                0 => last.Date == now.Date,                       // daily
                1 => since.TotalDays < 6,                          // weekly
                2 => last.Hour == now.Hour && last.Date == now.Date, // hourly
                _ => since.TotalMinutes < 30,
            };
            if (alreadyDone) return false;
        }

        return p.Frequency switch
        {
            0 => now.Hour > p.Hour || (now.Hour == p.Hour && now.Minute >= p.Minute),
            1 => (int)now.DayOfWeek == p.DayOfWeek &&
                 (now.Hour > p.Hour || (now.Hour == p.Hour && now.Minute >= p.Minute)),
            2 => now.Minute >= p.Minute,
            _ => false,
        };
    }

    // ---------------------------------------------------------- enforcement

    /// <summary>Creates due snapshots and prunes past the retention count.</summary>
    public static async Task<List<string>> RunDueAsync(WinnasSession s, WinnasService service,
        bool force = false)
    {
        var log = new List<string>();
        var now = DateTime.Now;
        var deviceMap = await service.GetVolumeDeviceMapAsync(s);

        foreach (var policy in AppState.Settings.SnapshotPolicies)
        {
            if (!policy.Enabled) continue;
            if (!force && !IsDue(policy, now)) continue;

            var created = await service.CreateSnapshotAsync(s, policy.Volume);
            if (!created.Ok)
            {
                policy.LastError = created.Message;
                log.Add($"{policy.Volume}：创建失败 — {created.Message}");
                continue;
            }

            policy.LastRun = now;
            policy.LastError = null;
            log.Add($"{policy.Volume}：已创建快照");

            var pruned = await PruneAsync(s, service, policy, deviceMap);
            if (pruned > 0) log.Add($"{policy.Volume}：清理了 {pruned} 个超出保留数的旧快照");
        }

        AppState.Save();
        return log;
    }

    /// <summary>Deletes snapshots on a volume beyond the policy's retention.</summary>
    private static async Task<int> PruneAsync(WinnasSession s, WinnasService service,
        SnapshotPolicy policy, Dictionary<string, string> deviceMap)
    {
        if (policy.Keep <= 0) return 0;

        var all = await service.GetSnapshotsAsync(s);
        var mine = all
            .Where(x => VolumeMatches(x.VolumeName, policy.Volume, deviceMap))
            .OrderByDescending(x => x.InstallDate ?? DateTime.MinValue)
            .ToList();

        if (mine.Count <= policy.Keep) return 0;

        var doomed = mine.Skip(policy.Keep).ToList();
        int removed = 0;
        foreach (var snap in doomed)
        {
            var result = await service.DeleteSnapshotAsync(s, snap.Id);
            if (result.Ok) removed++;
        }
        return removed;
    }

    /// <summary>
    /// Win32_ShadowCopy.VolumeName is a device path (\\?\Volume{guid}\) while
    /// policies are written as drive letters, so resolve through the volume map
    /// and only fall back to a loose text match when that lookup fails.
    /// </summary>
    private static bool VolumeMatches(string volumeName, string policyVolume,
        Dictionary<string, string> deviceMap)
    {
        if (string.IsNullOrWhiteSpace(volumeName) || string.IsNullOrWhiteSpace(policyVolume)) return false;

        if (policyVolume.Length >= 1 && deviceMap.TryGetValue(policyVolume[..1], out var devicePath))
            return string.Equals(volumeName.TrimEnd('\\'), devicePath.TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase);

        var letter = policyVolume.Length >= 2 ? policyVolume[..2] : policyVolume;
        if (volumeName.Contains(letter, StringComparison.OrdinalIgnoreCase)) return true;

        return volumeName.TrimEnd('\\')
            .EndsWith(policyVolume.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------- scheduled task wiring

    /// <summary>
    /// Whether the hourly trigger exists ON THE TARGET. Querying the local
    /// machine here would report a task that has nothing to do with the NAS.
    /// </summary>
    public static bool IsTaskRegistered(WinnasSession? s)
    {
        if (s is null) return false;

        // Every page refresh asks this question, and each ask spawns a
        // process. Hold the answer for a minute instead.
        if (s.Cache.TryGet<bool>("snapshotTask", TimeSpan.FromSeconds(60), out var cached))
            return cached;

        var (code, _, _) = Schtasks.Run(s, $"/Query /TN \"{TaskName}\"", 15000);
        var registered = code == 0;
        s.Cache.Set("snapshotTask", registered);
        return registered;
    }

    /// <summary>
    /// Registers the hourly trigger on the managed machine. A remote target
    /// gets its own copy of this EXE plus the policy list, because the paths on
    /// the console mean nothing over there.
    /// </summary>
    public static async Task<OpResult> RegisterTaskAsync(WinnasSession s)
    {
        var (deploy, exe) = await AgentDeployer.EnsureAsync(s);
        if (!deploy.Ok) return deploy;

        // A local task reads the same config.json this console uses. Only a
        // remote agent needs the policy list carried over.
        if (s.IsRemote)
        {
            var sync = await AgentDeployer.WritePoliciesAsync(s, AppState.Settings.SnapshotPolicies);
            if (!sync.Ok) return sync;
        }

        // HOURLY with MO 1 is the finest granularity schtasks accepts; the
        // per-policy check inside --snapshot decides what actually runs.
        var args = $"/Create /TN \"{TaskName}\" /TR \"\\\"{exe}\\\" --snapshot\" " +
                   "/SC HOURLY /MO 1 /RL HIGHEST /F";

        var (code, stdout, stderr) = Schtasks.Run(s, args, 25000);
        if (code != 0)
            return OpResult.Fail($"注册计划任务失败（{code}）：{FirstLine(stderr, stdout)}");

        s.Cache.Invalidate("snapshotTask");
        var where = s.IsRemote ? $"被控端 {s.Profile.Target}" : "本机";
        return OpResult.Good($"已在{where}注册计划任务「{TaskName}」，每小时检查一次策略");
    }

    public static OpResult UnregisterTask(WinnasSession s)
    {
        var (code, stdout, stderr) = Schtasks.Run(s, $"/Delete /TN \"{TaskName}\" /F", 20000);
        if (code != 0) return OpResult.Fail($"删除计划任务失败（{code}）：{FirstLine(stderr, stdout)}");

        s.Cache.Invalidate("snapshotTask");
        return OpResult.Good("已删除计划任务「" + TaskName + "」");
    }

    private static string FirstLine(params string[] sources)
    {
        foreach (var s in sources)
        {
            if (string.IsNullOrWhiteSpace(s)) continue;
            foreach (var line in s.Split('\n'))
            {
                var t = line.Trim();
                if (t.Length > 0) return t;
            }
        }
        return "无输出";
    }
}
