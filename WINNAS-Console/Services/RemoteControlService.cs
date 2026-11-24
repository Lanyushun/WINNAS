using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Management;
using Winnas.Console.Infrastructure;
using Winnas.Console.Models;

namespace Winnas.Console.Services;

/// <summary>
/// Remote control of the managed machine.
///
/// Everything here acts on the TARGET, never on the PC running this console.
/// That is the whole point of the app: the desktop is the remote control, the
/// NAS is what gets controlled. Power actions go through Win32_OperatingSystem
/// where a proper method exists, and through Win32_Process.Create elsewhere —
/// which means a remote target executes them in its own session, not ours.
/// </summary>
public static class RemoteControlService
{
    /// <summary>Scratch directory on the *target*; output files are read back through the admin share.</summary>
    public static string RemoteDir => @"C:\ProgramData\WINNAS-Console\remote";

    private static readonly string[] ProtectedProcesses =
    {
        "system", "system idle process", "registry", "memory compression", "smss.exe",
        "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe", "lsass.exe",
        "lsaiso.exe", "fontdrvhost.exe", "dwm.exe", "sihost.exe", "ctfmon.exe",
    };

    // -------------------------------------------------------------- processes

    public static Task<List<RemoteProcess>> GetProcessesAsync(WinnasSession s) => Task.Run(() =>
        s.Query(Cim.CimV2, "SELECT * FROM Win32_Process")
         .Select(x =>
         {
             var name = Cim.Str(x, "Name");
             return new RemoteProcess
             {
                 Name = name,
                 Pid = Cim.Int(x, "ProcessId"),
                 Memory = Cim.Long(x, "WorkingSetSize"),
                 ExecutablePath = NullStr(x, "ExecutablePath"),
                 CommandLine = NullStr(x, "CommandLine"),
                 Created = Cim.Date(x, "CreationDate"),
                 Protected = IsProtected(name),
             };
         })
         .OrderByDescending(p => p.Memory)
         .ToList());

    private static bool IsProtected(string name) =>
        ProtectedProcesses.Contains(name.Trim().ToLowerInvariant());

    private static string? NullStr(ManagementBaseObject i, string name)
    {
        var v = Cim.Str(i, name);
        return v.Length == 0 ? null : v;
    }

    public static Task<OpResult> KillProcessAsync(WinnasSession s, RemoteProcess p) => Task.Run(() =>
    {
        if (p.Protected || p.Pid <= 4)
            return OpResult.Fail($"{p.Name}（PID {p.Pid}）是系统进程，拒绝结束。结束它会导致被控端立即蓝屏或重启。");

        var inst = s.Query(Cim.CimV2, $"SELECT * FROM Win32_Process WHERE ProcessId={p.Pid}").FirstOrDefault();
        if (inst is null) return OpResult.Fail($"PID {p.Pid} 已不存在（进程可能已经退出）。");

        var r = s.InvokeInstance(inst, "Terminate", new Dictionary<string, object>());
        var rc = r.ReturnCode();
        return rc == 0
            ? OpResult.Good($"已结束 {p.Name}（PID {p.Pid}）")
            : OpResult.Fail($"结束失败，返回 0x{rc:X8}。远程结束进程需要被控端的管理员权限。");
    });

    // --------------------------------------------------------------- services

    public static Task<List<RemoteService>> GetServicesAsync(WinnasSession s) => Task.Run(() =>
        s.Query(Cim.CimV2, "SELECT * FROM Win32_Service")
         .Select(x => new RemoteService
         {
             Name = Cim.Str(x, "Name"),
             DisplayName = Cim.Str(x, "DisplayName"),
             State = Cim.Str(x, "State"),
             StartMode = Cim.Str(x, "StartMode"),
             AcceptStop = Cim.Bool(x, "AcceptStop"),
             AcceptPause = Cim.Bool(x, "AcceptPause"),
             PathName = NullStr(x, "PathName"),
         })
         .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
         .ToList());

    public static Task<OpResult> ServiceAsync(WinnasSession s, RemoteService svc, string action) => Task.Run(() =>
    {
        var inst = s.Query(Cim.CimV2, $"SELECT * FROM Win32_Service WHERE Name='{Escape(svc.Name)}'")
                    .FirstOrDefault();
        if (inst is null) return OpResult.Fail($"在被控端上找不到服务 {svc.Name}。");

        return action switch
        {
            "start" => Call(s, inst, "StartService", $"已启动 {svc.Name}"),
            "stop" => Call(s, inst, "StopService", $"已停止 {svc.Name}"),
            "pause" => Call(s, inst, "PauseService", $"已暂停 {svc.Name}"),
            "resume" => Call(s, inst, "ResumeService", $"已恢复 {svc.Name}"),
            _ => OpResult.Fail("未知的服务操作：" + action),
        };
    });

    /// <summary>ChangeStartMode takes the mode as an argument, unlike start/stop.</summary>
    public static Task<OpResult> SetStartModeAsync(WinnasSession s, RemoteService svc, string mode) => Task.Run(() =>
    {
        var inst = s.Query(Cim.CimV2, $"SELECT * FROM Win32_Service WHERE Name='{Escape(svc.Name)}'")
                    .FirstOrDefault();
        if (inst is null) return OpResult.Fail($"在被控端上找不到服务 {svc.Name}。");

        var r = s.InvokeInstance(inst, "ChangeStartMode",
            new Dictionary<string, object> { ["StartMode"] = mode });

        var rc = r.ReturnCode();
        var label = mode switch { "Auto" => "自动", "Manual" => "手动", "Disabled" => "禁用", _ => mode };
        return rc == 0
            ? OpResult.Good($"已将 {svc.Name} 的启动类型改为「{label}」")
            : OpResult.Fail($"修改失败，返回 0x{rc:X8}");
    });

    private static OpResult Call(WinnasSession s, ManagementBaseObject inst, string method, string okText)
    {
        var r = s.InvokeInstance(inst, method, new Dictionary<string, object>());
        var rc = r.ReturnCode();
        return rc == 0 ? OpResult.Good(okText) : OpResult.Fail(ServiceError(rc, method));
    }

    private static string ServiceError(uint rc, string method) => rc switch
    {
        1 => "不支持该操作",
        2 => "访问被拒绝（需要被控端的管理员权限）",
        3 => "被依赖的服务无法停止",
        4 => "无效的服务控制请求",
        5 => "服务无法接受该控制指令（可能正在启动或停止）",
        6 => "服务未启动",
        7 => "服务请求超时",
        8 => "未知的服务错误",
        9 => "找不到路径",
        10 => "服务已运行",
        11 => "服务数据库被锁定",
        12 => "服务依赖已被删除",
        13 => "服务依赖失败",
        14 => "服务已被禁用",
        15 => "服务登录失败（检查服务的运行账户密码）",
        16 => "服务标记为删除",
        _ => $"{method} 失败，返回 0x{rc:X8}",
    };

    // ----------------------------------------------------------------- power

    /// <summary>
    /// Power actions. Win32Shutdown handles the ones Windows exposes properly;
    /// hibernate/sleep/lock have no WMI method, so they go through a process on
    /// the target. A delay is only possible via shutdown.exe, used for that case.
    /// </summary>
    public static Task<OpResult> PowerAsync(WinnasSession s, PowerAction action,
        int delaySeconds = 0, bool force = true, string reason = "") => Task.Run(() =>
    {
        reason = reason.Trim();
        var note = reason.Length == 0 ? "WINNAS 控制台远程操作" : reason;

        switch (action)
        {
            case PowerAction.Abort:
                return Create(s, "shutdown.exe /a");

            case PowerAction.Hibernate:
                return Create(s, "shutdown.exe /h");

            case PowerAction.Sleep:
                return Create(s, "rundll32.exe powrprof.dll,SetSuspendState 0,1,0");

            case PowerAction.Lock:
                return Create(s, "rundll32.exe user32.dll,LockWorkStation");

            case PowerAction.Logoff when delaySeconds > 0:
                return Create(s, "shutdown.exe /l", "已注销被控端的登录会话");

            case PowerAction.Logoff:
                return Win32Shutdown(s, force ? 4u : 0u, "注销所有登录会话");

            case PowerAction.Shutdown when delaySeconds > 0:
                return Create(s, $"shutdown.exe /s /t {delaySeconds} /c \"{note}\"" + (force ? " /f" : ""),
                    $"{delaySeconds} 秒后关闭被控端");

            case PowerAction.Shutdown:
                return Win32Shutdown(s, force ? 5u : 1u, "关闭被控端");

            case PowerAction.Reboot when delaySeconds > 0:
                return Create(s, $"shutdown.exe /r /t {delaySeconds} /c \"{note}\"" + (force ? " /f" : ""),
                    $"{delaySeconds} 秒后重启被控端");

            case PowerAction.Reboot:
                return Win32Shutdown(s, force ? 6u : 2u, "重启被控端");

            default:
                return OpResult.Fail("不支持的电源操作");
        }
    });

    private static OpResult Win32Shutdown(WinnasSession s, uint flags, string okText)
    {
        // Flags: 0 logoff, 1 shutdown, 2 reboot, 4 forced logoff,
        // 5 forced shutdown, 6 forced reboot, 8 power off, 12 forced power off.
        var os = s.First(Cim.CimV2, "SELECT * FROM Win32_OperatingSystem WHERE Primary=TRUE")
                 ?? s.First(Cim.CimV2, "SELECT * FROM Win32_OperatingSystem");
        if (os is null) return OpResult.Fail("读不到被控端的 Win32_OperatingSystem，无法执行。");

        var r = s.InvokeInstance(os, "Win32Shutdown",
            new Dictionary<string, object> { ["Flags"] = flags, ["Reserved"] = 0u });

        var rc = r.ReturnCode();
        return rc == 0 ? OpResult.Good($"{okText}（被控端已收到指令）") : OpResult.Fail(ShutdownError(rc));
    }

    private static string ShutdownError(uint rc) => rc switch
    {
        1191 => "被控端上有另一个关机/重启正在等待执行，请先「取消待关机」",
        5 => "访问被拒绝（需要被控端的管理员权限，且远程 DCOM/WinRM 已放行）",
        1115 => "系统正在关机过程中，无法再接受该指令",
        _ => $"执行失败，返回 0x{rc:X8}",
    };

    // -------------------------------------------------------- remote command

    /// <summary>
    /// Runs a command on the target and reads its output back.
    ///
    /// Output is redirected to a file under ProgramData and read through the
    /// administrative share. That is the only approach that works identically
    /// for a local and a remote session: Win32_Process.Create cannot hand back
    /// stdout, and `chcp 65001` keeps Chinese output from turning to mojibake.
    /// </summary>
    public static async Task<RemoteCommandResult> RunCommandAsync(WinnasSession s, string command,
        int timeoutSeconds = 60)
    {
        var sw = Stopwatch.StartNew();
        command = (command ?? "").Trim();

        if (command.Length == 0)
            return new RemoteCommandResult { Message = "命令为空", Seconds = 0 };

        var dir = RemoteDir;
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var outFile = $@"{dir}\cmd-{stamp}.out";
        var fullLine = $@"cmd.exe /c chcp 65001 >nul & {command} > ""{outFile}"" 2>&1";

        try
        {
            // The scratch dir lives on the target, so create it through the share,
            // as the account this session was opened with.
            using (s.Impersonate())
                Directory.CreateDirectory(WinnasService.PathFor(s, dir));
        }
        catch (Exception ex)
        {
            return new RemoteCommandResult
            {
                Message = "无法在被控端上创建输出目录：" + OpResult.From(ex).Message,
                Seconds = sw.Elapsed.TotalSeconds,
            };
        }

        var (rc, pid) = CreateProcess(s, fullLine);
        if (rc != 0)
        {
            var hint = rc == 2
                ? "被控端拒绝创建进程。远程执行需要管理员权限，且远程 DCOM/WinRM 已放行；" +
                  "若用的是 DCOM，目标机上需存在与当前登录账户同名同密码的管理员账户。"
                : $"创建进程失败，返回 0x{rc:X8}";
            return new RemoteCommandResult { Message = hint, Seconds = sw.Elapsed.TotalSeconds };
        }

        var deadline = DateTime.Now.AddSeconds(Math.Clamp(timeoutSeconds, 5, 600));
        var exited = false;

        while (DateTime.Now < deadline)
        {
            await Task.Delay(700);
            if (pid == 0) { exited = true; break; }

            var alive = await Task.Run(() => SafeCount(s, pid));
            if (!alive) { exited = true; break; }
        }

        // The redirect target is flushed by the shell, but give it a beat.
        await Task.Delay(300);

        var output = ReadOutput(s, WinnasService.PathFor(s, outFile));
        sw.Stop();

        var text = output;
        if (!exited) text += $"\n\n[超时] 命令在 {timeoutSeconds} 秒内没有结束，以上是被控端已产生的输出。";

        return new RemoteCommandResult
        {
            Ok = exited,
            ExitCode = exited ? 0 : -1,
            Output = text.Trim(),
            CommandLine = command,
            Message = exited ? "已在被控端执行完成" : $"超过 {timeoutSeconds} 秒仍未结束",
            Seconds = sw.Elapsed.TotalSeconds,
            OutputPath = outFile,
        };
    }

    private static string ReadOutput(WinnasSession s, string path)
    {
        try
        {
            using var _ = s.Impersonate();
            if (!File.Exists(path)) return "(被控端没有产生任何输出)";

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var rd = new StreamReader(fs, System.Text.Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true);
            return rd.ReadToEnd();
        }
        catch (Exception ex)
        {
            return "(读取被控端输出失败：" + OpResult.From(ex).Message + ")";
        }
    }

    private static bool SafeCount(WinnasSession s, uint pid)
    {
        try
        {
            return s.Query(Cim.CimV2, $"SELECT ProcessId FROM Win32_Process WHERE ProcessId={pid}").Count > 0;
        }
        catch { return false; }
    }

    // ------------------------------------------------------------ messaging

    /// <summary>Pops a message box on the target's interactive session.</summary>
    public static Task<OpResult> NotifyAsync(WinnasSession s, string text, int seconds = 30) => Task.Run(() =>
    {
        var msg = (text ?? "").Trim();
        if (msg.Length == 0) return OpResult.Fail("消息内容为空。");

        // msg.exe needs the text quoted; internal quotes would break the line.
        var safe = msg.Replace("\"", "'").Replace("\r", " ").Replace("\n", " ");
        return Create(s, $@"msg.exe * /TIME:{Math.Clamp(seconds, 5, 3600)} ""{safe}""",
            "已向被控端发送通知");
    });

    // ----------------------------------------------------------- wake on lan

    public static Task<OpResult> WakeOnLanAsync(string? mac) => Task.Run(() =>
    {
        var bytes = ParseMac(mac);
        if (bytes is null) return OpResult.Fail("MAC 地址格式不对，应为 00-11-22-33-44-55 或 00:11:22:33:44:55。");

        var packet = new byte[6 + 16 * 6];
        for (var i = 0; i < 6; i++) packet[i] = 0xFF;
        for (var i = 1; i <= 16; i++) Array.Copy(bytes, 0, packet, i * 6, 6);

        try
        {
            using var udp = new UdpClient();
            udp.EnableBroadcast = true;
            udp.Send(packet, packet.Length, new IPEndPoint(IPAddress.Broadcast, 9));

            // A single datagram is easy to lose on a busy LAN.
            udp.Send(packet, packet.Length, new IPEndPoint(IPAddress.Broadcast, 7));
            return OpResult.Good($"已向 {mac} 发送唤醒包。开机需要几十秒，之后再用监控页确认。");
        }
        catch (Exception ex)
        {
            return OpResult.Fail("发送唤醒包失败：" + OpResult.From(ex).Message);
        }
    });

    public static byte[]? ParseMac(string? mac)
    {
        if (string.IsNullOrWhiteSpace(mac)) return null;

        var digits = mac.Where(char.IsAsciiHexDigit).ToArray();
        if (digits.Length != 12) return null;

        var result = new byte[6];
        for (var i = 0; i < 6; i++)
            result[i] = Convert.ToByte(new string(digits, i * 2, 2), 16);
        return result;
    }

    // ------------------------------------------------------------ helpers

    /// <summary>
    /// Starts a process on the TARGET. Locally this still goes through CIM so
    /// that there is exactly one code path to test and reason about.
    /// </summary>
    private static OpResult Create(WinnasSession s, string commandLine, string okText = "已在被控端执行")
    {
        var (rc, _) = CreateProcess(s, commandLine);
        return rc == 0 ? OpResult.Good(okText) : OpResult.Fail(CreateHint(rc));
    }

    private static string CreateHint(uint rc) => rc switch
    {
        2 => "被控端拒绝创建进程（需要管理员权限，且远程 DCOM/WinRM 已放行）",
        3 => "路径不存在",
        8 => "内存不足",
        9 => "被控端拒绝了命令行参数",
        21 => "参数无效",
        _ => $"创建进程失败，返回 0x{rc:X8}",
    };

    private static (uint Rc, uint Pid) CreateProcess(WinnasSession s, string commandLine)
    {
        try
        {
            var r = s.InvokeStatic(Cim.CimV2, "Win32_Process", "Create",
                new Dictionary<string, object> { ["CommandLine"] = commandLine });

            var rc = r.ReturnCode();
            if (rc != 0) return (rc, 0);

            var pid = 0u;
            try { pid = Convert.ToUInt32(r.Properties["ProcessId"]?.Value ?? 0); }
            catch { /* some targets omit it */ }

            return (0, pid);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            return (0xFFFFFFFF, 0);
        }
    }

    private static string Escape(string wql) => wql.Replace("\\", "\\\\").Replace("'", "\\'");

    /// <summary>Opens the built-in client. This runs HERE, pointing at the target.</summary>
    public static OpResult RemoteDesktop(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return OpResult.Fail("被控端没有主机名或 IP，无法连接远程桌面。");

        try
        {
            Process.Start(new ProcessStartInfo("mstsc.exe", $"/v:{host}") { UseShellExecute = true });
            return OpResult.Good($"已启动远程桌面，连接到 {host}");
        }
        catch (Exception ex)
        {
            return OpResult.Fail("无法启动 mstsc.exe：" + OpResult.From(ex).Message);
        }
    }
}
