using System.IO;
using System.Text;
using Winnas.Console.Models;

namespace Winnas.Console.Infrastructure;

/// <summary>
/// Headless entry points for the sync feature. They exist so the same checks
/// the window runs can be scripted, and so a drifted NAS clock can be fixed
/// without clicking through the UI (which is exactly when someone needs it).
/// </summary>
public static class SyncCli
{
    private static void Say(string line)
    {
        try { System.Console.OutputEncoding = Encoding.UTF8; } catch { /* no console */ }
        System.Console.WriteLine(line);
    }

    private static WinnasSession? Open(string which, bool verify, out int code)
    {
        var profile = ConnectionDiagnosticsCli.Pick(which);
        if (profile is null)
        {
            Say("找不到连接。参数可填 连接名 / 序号 / Id，留空用当前连接。");
            code = 2;
            return null;
        }

        try
        {
            code = 0;
            return WinnasSession.Open(profile, verify);
        }
        catch (Exception ex)
        {
            Say("连接失败：" + OpResult.From(ex).Message);
            code = 1;
            return null;
        }
    }

    // ------------------------------------------------------------ --synctime

    /// <summary>
    /// "--synctime [connection] [--check]" — print both clocks; unless --check
    /// is given, correct a drift larger than the configured tolerance.
    /// </summary>
    public static async Task<int> RunTimeAsync(string which, bool checkOnly)
    {
        var tolerance = TimeSync.ClampTolerance(AppState.Settings.TimeToleranceSeconds);

        using var session = Open(which, verify: true, out var code);
        if (session is null) return code;

        var state = await TimeSync.InspectAsync(session);
        Say($"本机时间   : {state.LocalText}");
        Say($"被控端时间 : {state.TargetText}");
        Say($"偏差       : {state.DriftText}（{state.DriftSeconds:+0;-0;0} 秒）");
        Say($"读取方式   : {state.Method}");
        Say($"容差       : {tolerance} 秒");

        if (!state.Ok)
        {
            Say("检查失败：" + state.Detail);
            return 1;
        }

        if (checkOnly)
        {
            Say(state.Drifted(tolerance)
                ? "结论：时钟不一致，需要校准（去掉 --check 即执行）。"
                : "结论：时钟一致，无需校准。");
            return state.Drifted(tolerance) ? 1 : 0;
        }

        if (!state.Drifted(tolerance))
        {
            Say("结论：时钟一致，未改动被控端。");
            return 0;
        }

        Say("正在校准…");
        var result = await TimeSync.CorrectAsync(session, session.Profile, tolerance);
        Say((result.Ok ? "完成：" : "失败：") + result.Message);
        return result.Ok ? 0 : 1;
    }

    // -------------------------------------------------------- --syncsettings

    /// <summary>
    /// "--syncsettings [push|pull|status|export|import] [connection] [path]"
    /// action defaults to status; path defaults to the file next to config.json.
    /// </summary>
    public static async Task<int> RunSettingsAsync(string[] operands)
    {
        var action = "status";
        var path = SettingsSync.DefaultExportPath;
        var rest = new List<string>();
        foreach (var o in operands)
        {
            if (o is "push" or "pull" or "status" or "export" or "import") { action = o; continue; }

            // A path must not be mistaken for a connection name. Without this,
            // "--syncsettings export C:\x.json" silently wrote to the default
            // path and the file the user named never appeared.
            if (o.Contains('/') || o.Contains('\\') ||
                o.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                Path.IsPathRooted(o))
            {
                path = o;
                continue;
            }
            rest.Add(o);
        }

        var which = rest.Count > 0 ? rest[0] : "";

        // export/import are local file operations: no connection needed.
        if (action == "export")
        {
            var r = SettingsSync.ExportTo(path);
            Say((r.Ok ? "完成：" : "失败：") + r.Message);
            return r.Ok ? 0 : 1;
        }

        if (action == "import")
        {
            var (read, snapshot) = SettingsSync.ImportFrom(path);
            if (snapshot is null)
            {
                Say("失败：" + read.Message);
                return 1;
            }
            Say($"读到来自 {snapshot.SourceMachine} 的副本（{snapshot.CapturedAt}）。");
            var applied = SettingsSync.Apply(snapshot);
            Say((applied.Ok ? "完成：" : "失败：") + applied.Message);
            return applied.Ok ? 0 : 1;
        }

        using var session = Open(which, verify: false, out var code);
        if (session is null) return code;

        switch (action)
        {
            case "push":
            {
                var r = await SettingsSync.PushAsync(session);
                Say((r.Ok ? "完成：" : "失败：") + r.Message);
                return r.Ok ? 0 : 1;
            }

            case "pull":
            {
                var (read, snapshot) = await SettingsSync.PullAsync(session);
                if (snapshot is null)
                {
                    Say("失败：" + read.Message);
                    return 1;
                }
                Say($"读到来自 {snapshot.SourceMachine} 的副本（{snapshot.CapturedAt}）。");
                var applied = SettingsSync.Apply(snapshot);
                Say((applied.Ok ? "完成：" : "失败：") + applied.Message);
                return applied.Ok ? 0 : 1;
            }

            default:
            {
                var info = await SettingsSync.InspectAsync(session);
                var local = SettingsSync.Capture();
                var same = info.Exists && info.Fingerprint == SettingsSync.Fingerprint(local);

                Say($"被控端副本 : {(info.Exists ? "存在" : "不存在")} — {info.Message}");
                if (info.Exists)
                {
                    Say($"写入时间   : {info.WrittenAt:yyyy-MM-dd HH:mm:ss}");
                    Say($"来源机器   : {info.SourceMachine}（采集于 {info.CapturedAt}）");
                    Say($"内容指纹   : {info.Fingerprint} / 本机 {SettingsSync.Fingerprint(local)}");
                    Say($"包含       : {info.Connections} 条连接、{info.BackupPlans} 个备份方案、{info.SnapshotPolicies} 条快照策略");
                }
                Say($"本机配置   : {AppState.FilePath}");
                Say($"本机内容   : {AppState.Settings.Connections.Count} 条连接、{AppState.Settings.BackupPlans.Count} 个备份方案、" +
                    $"{AppState.Settings.SnapshotPolicies.Count} 条快照策略、皮肤 {AppState.Settings.Theme}");
                Say("结论：" + (same ? "两端设置一致。" : info.Exists ? "两端设置不一致，可执行 push 或 pull。" : "尚未建立同步。"));

                var plans = await SettingsSync.InspectPlansAsync(session);
                Say($"被控端备份方案：{plans.Message}");
                return 0;
            }
        }
    }
}
