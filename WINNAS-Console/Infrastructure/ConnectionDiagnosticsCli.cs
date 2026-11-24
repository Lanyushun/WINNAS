using System.Text;
using Winnas.Console.Models;

namespace Winnas.Console.Infrastructure;

/// <summary>
/// "WINNAS.Console.exe --diag &lt;id|序号|名字&gt;" — prints the remote access
/// chain and exits non-zero when anything is red, so the same check can be run
/// from a script or from a support conversation without opening the window.
/// </summary>
public static class ConnectionDiagnosticsCli
{
    public static async Task<int> RunAsync(string which)
    {        var profile = Pick(which);
        if (profile is null)
        {
            Say("找不到匹配的连接。可用的有：");
            for (var i = 0; i < AppState.Settings.Connections.Count; i++)
            {
                var c = AppState.Settings.Connections[i];
                Say($"  [{i}] {c.Name} · {c.Target} · {c.TransportText}");
            }
            return 2;
        }

        Say($"诊断目标：{profile.Name}（{profile.Target} · {profile.TransportText}）");
        Say(new string('-', 60));

        var items = await ConnectionDiagnostics.RunAsync(profile);

        foreach (var item in items)
        {
            Say($"{item.Mark} {item.Name}：{item.Detail}");
            if (item.Fix.Length > 0)
            {
                foreach (var line in item.Fix.Split('\n'))
                    Say("    " + line.TrimEnd());
            }
        }

        Say(new string('-', 60));
        var fails = items.Count(i => i.Level == DiagLevel.Fail);
        var warns = items.Count(i => i.Level == DiagLevel.Warn);
        Say(fails == 0
            ? warns == 0 ? "结论：全部通过" : $"结论：可用，但有 {warns} 项需要注意"
            : $"结论：{fails} 项不通过，按上面的修复建议处理后重试");

        ActivityLog.Warn("连接", "执行连接诊断", $"{profile.Name}：{fails} 项失败 / {warns} 项警告");
        return fails == 0 ? 0 : 1;
    }

    /// <summary>
    /// "WINNAS.Console.exe --ping &lt;id&gt;" — opens the connection the same way
    /// the window does (including the verification probe) and reports the
    /// result. Exit code 0 means it really works, not just that a session
    /// object was created.
    /// </summary>
    public static Task<int> RunPingAsync(string which)
    {
        var profile = Pick(which);
        if (profile is null)
        {
            Say("找不到匹配的连接。");
            return Task.FromResult(2);
        }

        try
        {
            using var session = WinnasSession.Open(profile);
            Say($"✓ 连接成功：{profile.Name} → {session.MachineName}" +
                $"（{profile.Target} · {profile.TransportText} · 身份 {session.IdentityText}）");
            return Task.FromResult(0);
        }
        catch (Exception ex)
        {
            Say($"✕ 连接失败：{OpResult.From(ex).Message}");
            return Task.FromResult(1);
        }
    }

    /// <summary>
    /// "WINNAS.Console.exe --targetscript &lt;id&gt;" — prints the script to run on
    /// the NAS, exactly as the 「复制排查脚本」 button produces it. Having it on
    /// the command line means the script can be saved to a file and syntax
    /// checked without clicking, and it cannot drift from what the window gives
    /// the user.
    /// </summary>
    public static Task<int> RunScriptAsync(string which)
    {
        var profile = Pick(which);
        if (profile is null)
        {
            Say("找不到匹配的连接。");
            return Task.FromResult(2);
        }

        try
        {
            System.Console.OutputEncoding = Encoding.UTF8;
            System.Console.Write(Services.AiDiagnoseService.BuildTargetScript(profile));
        }
        catch { /* headless by design */ }

        return Task.FromResult(0);
    }

    /// <summary>
    /// "WINNAS.Console.exe --aidiag &lt;id&gt;" — runs the diagnostic and then
    /// streams the model's reading of it to the console. The same prompt the
    /// window uses, so this is a real end-to-end check of the AI path without
    /// clicking anything.
    /// </summary>
    public static async Task<int> RunAiAsync(string which)
    {
        var profile = Pick(which);
        if (profile is null)
        {
            Say("找不到匹配的连接。");
            return 2;
        }

        if (SecretVault.UnprotectText(AppState.Settings.AiApiKey).Length == 0)
        {
            Say("没有配置 AI API Key，先去「设置 → AI 助手」填一个。");
            return 2;
        }

        Say($"目标：{profile.Name}（{profile.Target} · {profile.TransportText}）");
        Say("正在诊断…");
        var items = await ConnectionDiagnostics.RunAsync(profile);

        var fails = items.Count(i => i.Level == DiagLevel.Fail);
        Say($"诊断完成：{fails} 项失败。正在让 {AppState.Settings.AiModel} 分析…");
        Say(new string('-', 60));

        var text = new StringBuilder();
        try
        {
            await foreach (var chunk in Services.AiDiagnoseService.StreamAsync(profile, items, CancellationToken.None))
            {
                text.Append(chunk);
                // Streaming to the console makes a hung request obvious; the
                // reply is printed again at the end for a clean copy.
                System.Console.Write(chunk);
            }
        }
        catch (Exception ex)
        {
            Say("");
            Say("AI 分析失败：" + OpResult.From(ex).Message);
            return 1;
        }

        Say("");
        Say(new string('-', 60));
        Say($"AI 分析结束，共 {text.Length} 字。");
        return 0;
    }

    /// <summary>
    /// "WINNAS.Console.exe --shares" — dumps MSFT_SmbShare exactly as the
    /// 共享 page sees it. The page showed zero rows while the overview counted
    /// two from the same query, so the raw result has to be visible.
    /// </summary>
    public static Task<int> RunSharesAsync(string which)
    {
        var profile = Pick(which);
        if (profile is null) { Say("找不到匹配的连接。"); return Task.FromResult(2); }

        try
        {
            using var s = WinnasSession.Open(profile);
            Say($"目标：{profile.Name} → {s.MachineName}");

            var rows = s.Query(Cim.SmbNs, "SELECT * FROM MSFT_SmbShare");
            Say($"{Cim.SmbNs} → MSFT_SmbShare 返回 {rows.Count} 行");
            foreach (var r in rows)
                Say($"  {Cim.Str(r, "Name"),-18} special={Cim.Bool(r, "Special"),-6} " +
                    $"state={Cim.Int(r, "ShareState")}  {Cim.Str(r, "Path")}");

            var old = s.Query(Cim.CimV2, "SELECT Name, Type FROM Win32_Share");
            Say($"{Cim.CimV2} → Win32_Share 返回 {old.Count} 行");
            foreach (var r in old)
                Say($"  {Cim.Str(r, "Name"),-18} type={Cim.Int(r, "Type")}");

            return Task.FromResult(0);
        }
        catch (Exception ex)
        {
            Say("失败：" + ex.Message);
            return Task.FromResult(1);
        }
    }

    internal static ConnectionProfile? Pick(string which)
    {
        var all = AppState.Settings.Connections;
        if (all.Count == 0) return null;

        if (string.IsNullOrWhiteSpace(which)) return AppState.Current();

        if (int.TryParse(which, out var index) && index >= 0 && index < all.Count) return all[index];

        return all.FirstOrDefault(c => string.Equals(c.Id, which, StringComparison.OrdinalIgnoreCase))
               ?? all.FirstOrDefault(c => c.Name.Contains(which, StringComparison.OrdinalIgnoreCase))
               ?? all.FirstOrDefault(c => c.Computer.Contains(which, StringComparison.OrdinalIgnoreCase));
    }

    private static void Say(string line)
    {
        try
        {
            System.Console.OutputEncoding = Encoding.UTF8;
            System.Console.WriteLine(line);
        }
        catch { /* headless by design */ }
    }
}
