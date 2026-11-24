using System.Windows;
using Winnas.Console.Infrastructure;
using Winnas.Console.Views;

namespace Winnas.Console;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        // WPF's generated Main() takes no parameters, so StartupEventArgs.Args
        // is always empty. Read the real command line instead.
        var args = Environment.GetCommandLineArgs();

        // Skin first: every window, and every screenshot taken later, is
        // rendered with the palette that is active from this point on.
        // "--skin <key>" overrides the saved choice for a single run.
        ThemeManager.Apply(AppState.Settings.Theme);
        var skinIdx = Array.FindIndex(args, a => string.Equals(a, "--skin", StringComparison.OrdinalIgnoreCase));
        if (skinIdx >= 0 && skinIdx + 1 < args.Length)
            ThemeManager.ApplyTransient(args[skinIdx + 1]);

        if (args.Any(a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            var code = await SelfTest.RunAsync();
            Shutdown(code);
            return;
        }

        // "--checkupdate" compares this build with the newest GitHub Release.
        // Exit codes: 0 = up to date / no release, 1 = newer version exists,
        // 2 = the API could not be reached or answered unexpectedly.
        if (args.Any(a => string.Equals(a, "--checkupdate", StringComparison.OrdinalIgnoreCase)))
        {
            Shutdown(await UpdateCli.RunAsync());
            return;
        }

        if (args.Any(a => string.Equals(a, "--snapshot", StringComparison.OrdinalIgnoreCase)))
        {
            var code = await SnapshotCli.RunAsync();
            Shutdown(code);
            return;
        }

        // "--shot <page> <file> [scroll]" renders one page to PNG for verification;
        // "--shotdiag" does the same but also runs the connection diagnostics.
        var shotIdx = Array.FindIndex(args, a =>
            string.Equals(a, "--shot", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "--shotdiag", StringComparison.OrdinalIgnoreCase));
        var shotDiag = shotIdx >= 0 && string.Equals(args[shotIdx], "--shotdiag", StringComparison.OrdinalIgnoreCase);
        if (shotIdx >= 0 && shotIdx + 2 < args.Length)
        {
            var ratio = shotIdx + 3 < args.Length
                        && double.TryParse(args[shotIdx + 3], out var r) ? r : 0;
            var code = await UiShot.RunAsync(args[shotIdx + 1], args[shotIdx + 2], ratio, shotDiag);
            Shutdown(code);
            return;
        }

        // "--runplan <id>" is what the registered Task Scheduler entry calls.
        var planIdx = Array.FindIndex(args, a => string.Equals(a, "--runplan", StringComparison.OrdinalIgnoreCase));
        if (planIdx >= 0 && planIdx + 1 < args.Length)
        {
            var code = await BackupCli.RunAsync(args[planIdx + 1]);
            Shutdown(code);
            return;
        }

        // "--aidiag <connection>" = --diag plus the model's reading of it.
        var aiDiagIdx = Array.FindIndex(args, a => string.Equals(a, "--aidiag", StringComparison.OrdinalIgnoreCase));
        if (aiDiagIdx >= 0)
        {
            var whichAi = aiDiagIdx + 1 < args.Length && !args[aiDiagIdx + 1].StartsWith("--") ? args[aiDiagIdx + 1] : "";
            var code = await ConnectionDiagnosticsCli.RunAiAsync(whichAi);
            Shutdown(code);
            return;
        }

        // "--targetscript <connection>" dumps the script meant to be run ON the
        // NAS — the same text the 「复制排查脚本」 button puts on the clipboard.
        var scriptIdx = Array.FindIndex(args, a => string.Equals(a, "--targetscript", StringComparison.OrdinalIgnoreCase));
        if (scriptIdx >= 0)
        {
            var whichScript = scriptIdx + 1 < args.Length && !args[scriptIdx + 1].StartsWith("--") ? args[scriptIdx + 1] : "";
            var code = await ConnectionDiagnosticsCli.RunScriptAsync(whichScript);
            Shutdown(code);
            return;
        }

        // "--authprobe <connection>" tries every account form the target might
        // accept — bare, ".\user", "MACHINE\user" — using the stored password,
        // over SMB first and then over WinRM and DCOM. Answers "which domain
        // string does this machine want" without anyone retyping a password.
        var probeIdx = Array.FindIndex(args, a => string.Equals(a, "--authprobe", StringComparison.OrdinalIgnoreCase));
        if (probeIdx >= 0)
        {
            var whichProbe = probeIdx + 1 < args.Length && !args[probeIdx + 1].StartsWith("--") ? args[probeIdx + 1] : "";
            Shutdown(AuthProbe.Run(whichProbe));
            return;
        }

        // "--ping <connection>" opens the connection exactly like the window
        // does, probe included, and exits non-zero when it does not work.
        var pingIdx = Array.FindIndex(args, a => string.Equals(a, "--ping", StringComparison.OrdinalIgnoreCase));
        if (pingIdx >= 0)
        {
            var whichPing = pingIdx + 1 < args.Length && !args[pingIdx + 1].StartsWith("--") ? args[pingIdx + 1] : "";
            var code = await ConnectionDiagnosticsCli.RunPingAsync(whichPing);
            Shutdown(code);
            return;
        }

        // "--diag [connectionId|index]" prints the whole remote-access chain,
        // so a failure that only shows as "access denied" in the UI can be
        // narrowed down without clicking through the app.
        var diagIdx = Array.FindIndex(args, a => string.Equals(a, "--diag", StringComparison.OrdinalIgnoreCase));
        if (diagIdx >= 0)
        {
            var which = diagIdx + 1 < args.Length && !args[diagIdx + 1].StartsWith("--")
                ? args[diagIdx + 1] : "";
            var code = await ConnectionDiagnosticsCli.RunAsync(which);
            Shutdown(code);
            return;
        }

        // "--exec <connection> <command>" runs PowerShell on the target over SMB
        // + the task scheduler. This is the only channel that still works when
        // WMI itself is denied — which is exactly when a repair is needed.
        var execIdx = Array.FindIndex(args, a => string.Equals(a, "--exec", StringComparison.OrdinalIgnoreCase));
        if (execIdx >= 0)
        {
            var whichExec = execIdx + 1 < args.Length && !args[execIdx + 1].StartsWith("--") ? args[execIdx + 1] : "";
            var command = execIdx + 2 < args.Length ? args[execIdx + 2] : "";
            var code = RemoteShell.RunCommand(whichExec, command);
            Shutdown(code);
            return;
        }

        // "--shares" dumps MSFT_SmbShare as the 共享 page sees it.
        var sharesIdx = Array.FindIndex(args, a => string.Equals(a, "--shares", StringComparison.OrdinalIgnoreCase));
        if (sharesIdx >= 0)
        {
            var whichShares = sharesIdx + 1 < args.Length && !args[sharesIdx + 1].StartsWith("--") ? args[sharesIdx + 1] : "";
            Shutdown(await ConnectionDiagnosticsCli.RunSharesAsync(whichShares));
            return;
        }

        // "--synctime [connection] [--check]" compares this console's clock
        // with the target's and, unless --check is given, corrects a drift
        // larger than the configured tolerance. The NAS once ran 22 days slow,
        // which made every security-log timestamp useless while triaging.
        var syncTimeIdx = Array.FindIndex(args, a => string.Equals(a, "--synctime", StringComparison.OrdinalIgnoreCase));
        if (syncTimeIdx >= 0)
        {
            var whichTime = syncTimeIdx + 1 < args.Length && !args[syncTimeIdx + 1].StartsWith("--")
                ? args[syncTimeIdx + 1] : "";
            var checkOnly = args.Skip(syncTimeIdx + 1)
                .Any(a => string.Equals(a, "--check", StringComparison.OrdinalIgnoreCase));
            var code = await SyncCli.RunTimeAsync(whichTime, checkOnly);
            Shutdown(code);
            return;
        }

        // "--syncsettings [push|pull|status|export|import] [connection] [path]"
        var syncSetIdx = Array.FindIndex(args, a => string.Equals(a, "--syncsettings", StringComparison.OrdinalIgnoreCase));
        if (syncSetIdx >= 0)
        {
            var operands = args.Skip(syncSetIdx + 1).Where(a => !a.StartsWith("--")).ToArray();
            var code = await SyncCli.RunSettingsAsync(operands);
            Shutdown(code);
            return;
        }

        base.OnStartup(e);
        new MainWindow().Show();
    }
}
