using System.Globalization;
using System.Management;
using Winnas.Console.Models;

namespace Winnas.Console.Infrastructure;

/// <summary>
/// One reading of the target's clock next to this console's clock.
///
/// Both values are "wall clock" (DateTimeKind.Unspecified deliberately). A NAS
/// that is 22 days behind does not have a timezone problem — it has a wrong
/// clock. Converting through UTC here would hide exactly that: the offset is
/// the same on both machines, so it cancels out and must NOT be applied.
/// </summary>
public sealed record ClockState(
    bool Ok,
    DateTime Local,
    DateTime? Target,
    double DriftSeconds,
    string Detail,
    string Method)
{
    /// <summary>Positive = target is ahead of this console.</summary>
    public string DriftText
    {
        get
        {
            var a = Math.Abs(DriftSeconds);
            var sign = DriftSeconds >= 0 ? "快" : "慢";
            if (a < 1) return "基本一致";
            if (a < 60) return $"{sign} {a:F0} 秒";
            if (a < 3600) return $"{sign} {a / 60:F1} 分钟";
            if (a < 86400) return $"{sign} {a / 3600:F1} 小时";
            return $"{sign} {a / 86400:F1} 天";
        }
    }

    public bool Drifted(double toleranceSeconds) => Ok && Math.Abs(DriftSeconds) > toleranceSeconds;

    public string LocalText => Local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    public string TargetText => Target?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "读取失败";
}

public static class TimeSync
{
    /// <summary>Allowed range for the tolerance setting, in seconds.</summary>
    public static int ClampTolerance(int seconds) => seconds is < 2 or > 86400 ? 30 : seconds;

    /// <summary>
    /// Read Win32_OperatingSystem.LocalDateTime from the target. System.Management
    /// may hand back either a DateTime or the raw DMTF string depending on the
    /// provider, so both are handled. For the DMTF string only the first 14
    /// digits are used: in a LocalDateTime value those ARE the wall clock on
    /// that machine, and the trailing +ooo is just its UTC offset annotation.
    /// </summary>
    public static ClockState Inspect(WinnasSession session)
    {
        var local = DateTime.SpecifyKind(DateTime.Now, DateTimeKind.Unspecified);

        if (!session.IsRemote)
            return new ClockState(false, local, null, 0,
                "当前连接是本机，没有可比对或校准的远端时钟。", "本机模式");

        try
        {
            var os = session.First(Cim.CimV2, "SELECT LocalDateTime FROM Win32_OperatingSystem")
                     ?? session.First(Cim.CimV2, "SELECT * FROM Win32_OperatingSystem");
            if (os is null)
                return new ClockState(false, local, null, 0,
                    "读不到被控端的 Win32_OperatingSystem，无法检查时间。", "WMI");

            var raw = os.Properties["LocalDateTime"]?.Value;
            DateTime? target = raw switch
            {
                DateTime dt => DateTime.SpecifyKind(dt, DateTimeKind.Unspecified),
                string s => ParseWallClock(s),
                _ => null,
            };

            if (target is null)
                return new ClockState(false, local, null, 0,
                    "被控端返回的 LocalDateTime 无法解析：" + (raw?.ToString() ?? "(空)"), "WMI");

            var drift = (target.Value - local).TotalSeconds;
            var state = new ClockState(true, local, target, drift, "",
                "WMI · Win32_OperatingSystem.LocalDateTime");
            return state with
            {
                Detail = $"本机 {state.LocalText} · 被控端 {state.TargetText}，偏差 {state.DriftText}",
            };
        }
        catch (Exception ex)
        {
            return new ClockState(false, local, null, 0,
                "检查被控端时间失败：" + OpResult.From(ex).Message, "WMI");
        }
    }

    public static Task<ClockState> InspectAsync(WinnasSession session) => Task.Run(() => Inspect(session));

    private static DateTime? ParseWallClock(string s)
    {
        if (s.Length < 14) return null;
        return DateTime.TryParseExact(s[..14], "yyyyMMddHHmmss",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? DateTime.SpecifyKind(dt, DateTimeKind.Unspecified)
            : null;
    }

    /// <summary>Build a DMTF datetime for SetDateTime from this console's wall clock.</summary>
    internal static string DmtfFor(DateTime wallClock)
    {
        var offset = (int)TimeZoneInfo.Local.GetUtcOffset(wallClock).TotalMinutes;
        var sign = offset >= 0 ? "+" : "-";
        return wallClock.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) +
               ".000000" + sign + Math.Abs(offset).ToString("000", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Set the target's clock to this console's local time. WMI SetDateTime is
    /// tried first (it is the channel that already works); if the provider
    /// refuses the privilege or the clock does not actually move, fall back to
    /// the SMB + Task Scheduler channel, which runs as SYSTEM and always holds
    /// SeSystemTimePrivilege.
    /// </summary>
    public static Task<OpResult> CorrectAsync(WinnasSession session, ConnectionProfile profile,
        double toleranceSeconds) => Task.Run(() =>
    {
        var before = Inspect(session);
        if (!before.Ok) return OpResult.Fail("无法校准：" + before.Detail);
        if (!before.Drifted(toleranceSeconds))
            return OpResult.Good($"时钟一致（偏差 {before.DriftText}），无需校准。");

        var dmtf = DmtfFor(before.Local);
        var wmiNote = "";

        // ---------------------------------------------------- attempt 1: WMI
        try
        {
            var os = session.First(Cim.CimV2, "SELECT * FROM Win32_OperatingSystem WHERE Primary=TRUE")
                     ?? session.First(Cim.CimV2, "SELECT * FROM Win32_OperatingSystem");
            if (os is not null)
            {
                var r = session.InvokeInstance(os, "SetDateTime",
                    new Dictionary<string, object> { ["LocalDateTime"] = dmtf });
                var rc = r.ReturnCode();
                if (rc == 0)
                {
                    Thread.Sleep(700);
                    var after = Inspect(session);
                    if (!after.Drifted(toleranceSeconds))
                        return OpResult.Good(
                            $"已通过 WMI 校准：被控端由 {before.TargetText} 调整为 {after.TargetText}（原偏差 {before.DriftText}）。");
                    wmiNote = "WMI 返回成功但时间没变";
                }
                else
                {
                    wmiNote = $"WMI SetDateTime 返回 {rc}（通常是缺少 SeSystemTimePrivilege）";
                }
            }
            else wmiNote = "读不到 Win32_OperatingSystem 实例";
        }
        catch (Exception ex)
        {
            wmiNote = "WMI 调用异常：" + OpResult.From(ex).Message;
        }

        // ------------------------------------------- attempt 2: SYSTEM shell
        if (profile.IsLocal)
            return OpResult.Fail($"校准失败：{wmiNote}，且本机连接无法使用远程执行通道。");

        var stamp = before.Local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var script =
            $"$d = [datetime]::ParseExact('{stamp}','yyyy-MM-dd HH:mm:ss',[Globalization.CultureInfo]::InvariantCulture)\r\n" +
            "Set-Date -Date $d | Out-Null\r\n" +
            "Get-Date -Format 'yyyy-MM-dd HH:mm:ss'";

        var shell = RemoteShell.RunScript(profile, script, timeoutSeconds: 120);
        if (!shell.Ok)
            return OpResult.Fail($"校准失败：{wmiNote}；远程执行通道也失败（{shell.Message}）。");

        Thread.Sleep(700);
        var verified = Inspect(session);
        if (verified.Drifted(toleranceSeconds))
            return OpResult.Fail(
                $"远程脚本已执行，但被控端时间仍是 {verified.TargetText}（偏差 {verified.DriftText}）。" +
                "检查被控端是否有策略或服务在强制回写时间。");

        return OpResult.Good(
            $"已通过 SYSTEM 任务校准：被控端由 {before.TargetText} 调整为 {verified.TargetText}" +
            $"（原偏差 {before.DriftText}；{wmiNote}）。");
    });

    /// <summary>
    /// What runs after every successful remote connect. Deliberately quiet when
    /// nothing needs doing: a monitoring tool that pops a toast on every launch
    /// is noise. Only an actual correction or a failure is worth reporting.
    /// </summary>
    public static async Task<(bool Corrected, OpResult Result)> AutoAsync(WinnasSession session, ConnectionProfile profile)
    {
        if (!AppState.Settings.TimeAutoSync || !session.IsRemote)
            return (false, OpResult.Good("自动校时未开启或当前是本机连接，跳过。"));

        var tolerance = ClampTolerance(AppState.Settings.TimeToleranceSeconds);
        var state = await InspectAsync(session);
        if (!state.Ok)
        {
            ActivityLog.Warn("校时", "无法检查被控端时钟", state.Detail);
            return (false, OpResult.Fail("无法检查被控端时钟：" + state.Detail));
        }

        if (!state.Drifted(tolerance))
            return (false, OpResult.Good($"时钟一致（偏差 {state.DriftText}，容差 {tolerance} 秒）。"));

        ActivityLog.Warn("校时", $"检测到时钟偏差 {state.DriftText}",
            $"本机 {state.LocalText} / 被控端 {state.TargetText}");

        var result = await CorrectAsync(session, profile, tolerance);
        ActivityLog.Write(result.Ok ? LogLevel.Ok : LogLevel.Error, "校时",
            result.Ok ? "自动校时完成" : "自动校时失败", result.Message);
        return (result.Ok, result);
    }
}
