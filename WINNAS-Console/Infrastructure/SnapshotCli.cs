using System.IO;
using System.Text;
using Winnas.Console.Models;
using Winnas.Console.Services;

namespace Winnas.Console.Infrastructure;

/// <summary>
/// Headless run of the snapshot policies: "WINNAS.Console.exe --snapshot".
///
/// This is what the scheduled task invokes. It never opens a window, writes
/// what it did to a log file, and exits 0 even when a single policy fails —
/// a non-zero exit would make Task Scheduler report a failure the user has to
/// dismiss, and one bad volume should not look like a broken task.
/// </summary>
public static class SnapshotCli
{
    private const int MaxLogLines = 500;

    public static async Task<int> RunAsync()
    {
        var lines = new List<string>
        {
            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 开始检查定时快照策略",
        };

        // Deployed as an agent on the managed machine? Then the policy list
        // came over as a file next to this EXE, not from a console config.
        AgentDeployer.AdoptSidecarPolicies();

        var profile = AppState.Current();
        lines.Add($"目标：{profile.Name}（{profile.Target} · {profile.TransportText}）");

        var settings = AppState.Settings;
        var total = settings.SnapshotPolicies.Count;
        var enabled = settings.SnapshotPolicies.Count(p => p.Enabled);
        lines.Add($"策略总数 {total}，启用 {enabled}");

        try
        {
            using var session = WinnasSession.Open(profile);
            var service = new WinnasService();

            var log = await SnapshotPolicyService.RunDueAsync(session, service);
            if (log.Count == 0) lines.Add("本次没有到期的策略");
            else lines.AddRange(log.Select(x => "  · " + x));
        }
        catch (Exception ex)
        {
            lines.Add("  ! 执行失败：" + OpResult.From(ex).Message);
        }

        lines.Add($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 检查结束");
        lines.Add("");

        Write(lines);

        try
        {
            // No console exists when Task Scheduler starts this; ignore.
            // Fully qualified: inside Winnas.Console.* "Console" would resolve
            // to our own namespace first.
            System.Console.OutputEncoding = Encoding.UTF8;
            System.Console.WriteLine(string.Join(Environment.NewLine, lines));
        }
        catch { /* headless by design */ }

        return 0;
    }

    private static void Write(List<string> lines)
    {
        try
        {
            Directory.CreateDirectory(AppState.Dir);
            var path = Path.Combine(AppState.Dir, "snapshot.log");

            var existing = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
            existing.AddRange(lines);

            if (existing.Count > MaxLogLines)
                existing = existing.Skip(existing.Count - MaxLogLines).ToList();

            File.WriteAllLines(path, existing);
        }
        catch { /* logging must never break the scheduled run */ }
    }
}
