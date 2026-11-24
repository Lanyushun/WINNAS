using System.Diagnostics;
using System.IO;
using System.Text;
using Winnas.Console.Models;
using Winnas.Console.Services;

namespace Winnas.Console.Infrastructure;

/// <summary>
/// Runs a script on the target through the two channels that do not involve
/// WMI at all: SMB (to drop the script and collect the output) and the task
/// scheduler (to make the target execute it).
///
/// This exists because WMI can be broken while everything else works. When
/// DCOM activation is denied, every page that reads a CIM class goes dark —
/// including the one button that could fix the machine. SMB answered fine on
/// the same credentials in exactly that situation, so the fix has to be able
/// to travel over SMB.
///
/// The task runs as SYSTEM: the deploying account's own rights are not always
/// enough to change machine-wide settings, and SYSTEM needs no password to be
/// stored in a task definition.
/// </summary>
public static class RemoteShell
{
    private const string WorkDir = @"C:\ProgramData\WINNAS-Console\tmp";

    public sealed record ShellResult(bool Ok, string Output, string Message);

    public static ShellResult RunScript(ConnectionProfile profile, string script, int timeoutSeconds = 180)
    {
        if (profile.IsLocal)
            return new ShellResult(false, "", "本机连接不需要远程执行，直接跑脚本即可。");

        var host = profile.Target.Trim();
        if (host.Length == 0)
            return new ShellResult(false, "", "这条连接没有填主机名。");

        // One stamp for all three scratch files so a stale one can never be
        // mistaken for this run's output.
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var ps1 = $@"{WorkDir}\run-{stamp}.ps1";
        var outFile = $@"{WorkDir}\run-{stamp}.out";
        var doneFile = $@"{WorkDir}\run-{stamp}.done";
        var taskName = $"WINNAS-Tmp-{stamp}";

        WinnasSession? session = null;
        try
        {
            // verify:false on purpose — this class is the fallback for when the
            // session cannot answer anything, so it must not insist on an answer.
            session = WinnasSession.Open(profile, verify: false);

            using (session.Impersonate())
            {
                Directory.CreateDirectory(WinnasService.PathFor(session, WorkDir));
                WriteRemote(session, ps1, BuildWrapper(script, outFile, doneFile));
            }

            // No inner quotes around the script path: schtasks has a famously
            // broken /tr parser (it treats \" as a literal), and the path is
            // chosen by this class, so it never contains a space.
            var create = Schtasks.Run(session,
                $"/create /tn \"{taskName}\" /tr \"powershell.exe -NoProfile -ExecutionPolicy Bypass " +
                $"-File {ps1}\" /sc ONCE /st 00:00 /ru \"NT AUTHORITY\\SYSTEM\" /rl HIGHEST /f");

            if (create.Code != 0)
                return new ShellResult(false, "", ExplainSchtasks(create));

            var run = Schtasks.Run(session, $"/run /tn \"{taskName}\"");
            if (run.Code != 0)
            {
                DeleteTask(session, taskName);
                return new ShellResult(false, "", "任务建好了却没法启动：" + Combined(run));
            }

            // Poll the marker file rather than the output file: the output is
            // written first, so a half-flushed file would read as a short answer.
            var deadline = DateTime.Now.AddSeconds(Math.Clamp(timeoutSeconds, 10, 900));
            var completed = false;
            while (DateTime.Now < deadline)
            {
                Thread.Sleep(700);
                try
                {
                    using var _ = session.Impersonate();
                    if (File.Exists(WinnasService.PathFor(session, doneFile))) { completed = true; break; }
                }
                catch { /* share momentarily unavailable while the task starts */ }
            }

            string output;
            try
            {
                using var _ = session.Impersonate();
                var mapped = WinnasService.PathFor(session, outFile);
                output = File.Exists(mapped) ? File.ReadAllText(mapped, Encoding.UTF8) : "";
            }
            catch (Exception ex)
            {
                output = "";
                return new ShellResult(false, "", "任务跑完了，但取回输出失败：" + OpResult.From(ex).Message);
            }

            return completed
                ? new ShellResult(true, output.Trim(), "执行完成")
                : new ShellResult(false, output.Trim(),
                    $"等了 {timeoutSeconds} 秒任务还没结束。可能是脚本卡住了，或被控端的任务计划程序没启动。");
        }
        catch (Exception ex)
        {
            return new ShellResult(false, "", "远程执行失败：" + OpResult.From(ex).Message);
        }
        finally
        {
            if (session is not null)
            {
                DeleteTask(session, taskName);
                TryDelete(session, ps1, outFile, doneFile);
                session.Dispose();
            }
        }
    }

    /// <summary>
    /// PowerShell 5.1 reads a BOM-less UTF-8 file as GBK, which turns every
    /// Chinese character in the script into mojibake — and it fails at parse
    /// time, not at run time, so the symptom is an empty output file.
    /// </summary>
    private static void WriteRemote(WinnasSession session, string localPath, string content)
    {
        // The caller is already impersonating, so the mapped UNC path carries
        // the deploying account's credentials.
        var mapped = WinnasService.PathFor(session, localPath);
        File.WriteAllText(mapped, content, new UTF8Encoding(true));
    }

    private static string BuildWrapper(string script, string outFile, string doneFile) =>
        "[Console]::OutputEncoding = [Text.Encoding]::UTF8\r\n" +
        "$ProgressPreference = 'SilentlyContinue'\r\n" +
        "& {\r\n" +
        script.Replace("\r\n", "\n").Replace("\n", "\r\n") + "\r\n" +
        "} *>&1 | Out-File -FilePath '" + outFile + "' -Encoding utf8\r\n" +
        "'ok' | Out-File -FilePath '" + doneFile + "' -Encoding ascii\r\n";

    private static void DeleteTask(WinnasSession session, string taskName)
    {
        try { Schtasks.Run(session, $"/delete /tn \"{taskName}\" /f", 10000); }
        catch { /* a leftover empty task is harmless; the run already happened */ }
    }

    private static void TryDelete(WinnasSession session, params string[] paths)
    {
        try
        {
            using var _ = session.Impersonate();
            foreach (var p in paths)
            {
                var mapped = WinnasService.PathFor(session, p);
                if (File.Exists(mapped)) File.Delete(mapped);
            }
        }
        catch { /* scratch files only */ }
    }

    private static string ExplainSchtasks((int Code, string Out, string Err) r) => r.Code switch
    {
        1 => "被控端拒绝了这次任务创建。任务计划程序依赖 RPC，若连它也拒绝，" +
             "说明限制范围比 WMI 更大——只能到被控端本机操作：" + Combined(r),
        // 1326 / 5 surface as generic failures inside schtasks' own output.
        _ => $"创建远程任务失败（退出码 {r.Code}）：" + Combined(r),
    };

    private static string Combined((int Code, string Out, string Err) r)
    {
        var text = (r.Out + " " + r.Err).Trim();
        return text.Length == 0 ? "（无输出）" : text.Replace("\r", "").Replace("\n", " ");
    }

    // ------------------------------------------------------------- CLI entry

    /// <summary>
    /// "WINNAS.Console.exe --exec &lt;id|序号|名字&gt; &lt;command&gt;" — the same
    /// channel as a one-shot, so a repair can be applied without the window.
    /// The command is treated as PowerShell, not cmd.
    /// </summary>
    public static int RunCommand(string which, string command)
    {
        var all = AppState.Settings.Connections;
        var profile = string.IsNullOrWhiteSpace(which) ? AppState.Current() : Pick(all, which);

        if (profile is null)
        {
            System.Console.WriteLine("找不到匹配的连接。");
            for (var i = 0; i < all.Count; i++)
                System.Console.WriteLine($"  [{i}] {all[i].Name} · {all[i].Target} · {all[i].TransportText}");
            return 2;
        }

        if (string.IsNullOrWhiteSpace(command))
        {
            System.Console.WriteLine("用法：--exec <连接> <PowerShell 命令>");
            return 2;
        }

        System.Console.OutputEncoding = Encoding.UTF8;
        System.Console.WriteLine($"远程执行 → {profile.Name}（{profile.Target}）");
        System.Console.WriteLine(new string('-', 60));

        var sw = Stopwatch.StartNew();
        var result = RunScript(profile, command);

        if (result.Output.Length > 0) System.Console.WriteLine(result.Output);
        System.Console.WriteLine(new string('-', 60));
        System.Console.WriteLine($"{result.Message}（{sw.Elapsed.TotalSeconds:F1}s）");

        ActivityLog.Warn("连接", "远程执行", $"{profile.Name}：{result.Message}");
        return result.Ok ? 0 : 1;
    }

    private static ConnectionProfile? Pick(List<ConnectionProfile> all, string which)
    {
        if (int.TryParse(which, out var index) && index >= 0 && index < all.Count) return all[index];

        return all.FirstOrDefault(c => string.Equals(c.Id, which, StringComparison.OrdinalIgnoreCase))
               ?? all.FirstOrDefault(c => c.Name.Contains(which, StringComparison.OrdinalIgnoreCase))
               ?? all.FirstOrDefault(c => c.Computer.Contains(which, StringComparison.OrdinalIgnoreCase));
    }
}
