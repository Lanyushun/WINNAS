using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Winnas.Console.Models;

namespace Winnas.Console.Infrastructure;

public enum DiagLevel
{
    Ok,
    Warn,
    Fail,
    Info,
}

/// <summary>One line of the connection report.</summary>
public sealed record DiagItem(string Name, DiagLevel Level, string Detail, string Fix = "")
{
    public string Mark => Level switch
    {
        DiagLevel.Ok => "✓",
        DiagLevel.Warn => "!",
        DiagLevel.Fail => "✕",
        _ => "·",
    };

    public string Color => Level switch
    {
        DiagLevel.Ok => "#15803D",
        DiagLevel.Warn => "#B45309",
        DiagLevel.Fail => "#B91C1C",
        _ => "#475569",
    };
}

/// <summary>
/// Walks the whole chain a remote connection has to survive, in the order it
/// actually breaks: name → ports → credentials → WMI permission → UAC remote
/// token filtering → admin share. "Access denied" on its own says nothing about
/// which link failed, and every one of them has a different fix.
/// </summary>
public static class ConnectionDiagnostics
{
    private const uint Hklm = 0x80000002;
    private const string UacKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";

    public static Task<List<DiagItem>> RunAsync(ConnectionProfile profile) => Task.Run(() =>
    {
        var items = new List<DiagItem>();

        if (profile.IsLocal)
        {
            items.Add(new DiagItem("目标", DiagLevel.Ok,
                $"本机模式，直接读取 {Environment.MachineName}，不需要凭据", ""));
            return items;
        }

        if (profile.IsMisconfigured)
        {
            items.Add(new DiagItem("目标", DiagLevel.Fail,
                "选了远程传输，但没有填主机名——现在连接的实际是本机",
                "在「主机名或 IP」里填 NAS 的地址，例如 192.168.1.10 或 NASBOX。"));
            return items;
        }

        var host = profile.Computer.Trim();
        items.Add(new DiagItem("目标", DiagLevel.Info, $"{profile.Name} · {host} · {profile.TransportText}"));

        // ---------------------------------------------------------- name
        var addresses = Resolve(host, out var resolveError);
        items.Add(addresses.Count > 0
            ? new DiagItem("名称解析", DiagLevel.Ok, $"{host} → {string.Join(", ", addresses)}")
            : new DiagItem("名称解析", DiagLevel.Fail,
                $"{host} 解析不到地址：{resolveError}",
                "直接填 IP 地址可跳过名称解析，例如 192.168.1.10。"));

        if (addresses.Count == 0) return items;

        // --------------------------------------------------------- ports
        var target = addresses[0];
        var p135 = Probe(target, 135);
        var p445 = Probe(target, 445);
        var p3389 = Probe(target, 3389);

        // Every remote connection is WMI over DCOM now — the WinRM transport
        // was removed once it was measured: MI's WSMan path could not
        // authenticate against this NAS at all, while classic DCOM went
        // straight through. Probing 5985 here would only suggest a fix that
        // no longer has anything behind it.
        items.Add(p135
            ? new DiagItem("DCOM 端点映射 135/tcp", DiagLevel.Ok, "端口通")
            : new DiagItem("DCOM 端点映射 135/tcp", DiagLevel.Fail,
                "端口不通，WMI 请求根本到不了被控端",
                "被控端以管理员运行（按资源 ID 匹配，中文系统也有效）：\n" +
                "$g = @('@FirewallAPI.dll,-34251')\n" +
                "Get-NetFirewallRule | Where-Object { $g -contains $_.Group } | Enable-NetFirewallRule"));

        items.Add(p445
            ? new DiagItem("SMB 445/tcp", DiagLevel.Ok, "端口通（文件页、日志、执行器部署依赖它）")
            : new DiagItem("SMB 445/tcp", DiagLevel.Warn,
                "端口不通：文件页、日志读取、定时任务执行器部署都会失败",
                "被控端放行文件和打印机共享那一组规则。"));

        // Not a requirement — but when WMI is the thing that is broken, this is
        // how the user gets to the console that fixes it. Reporting it here, at
        // the moment they are staring at a red chain, saves a round trip.
        items.Add(p3389
            ? new DiagItem("远程桌面 3389/tcp", DiagLevel.Ok,
                $"端口通：连不上时可以登进去手动处理（mstsc /v:{target}）")
            : new DiagItem("远程桌面 3389/tcp", DiagLevel.Info,
                "端口不通：被控端出问题时没法远程登入，只能到机器跟前操作",
                "被控端开启远程桌面：\n" +
                "Set-ItemProperty 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\Terminal Server' -Name fDenyTSConnections -Value 0\n" +
                "New-NetFirewallRule -DisplayName 'WINNAS-RDP' -Direction Inbound -Protocol TCP -LocalPort 3389 -Action Allow"));

        // ---------------------------------------------------- credentials
        var user = profile.UserName.Trim();
        NetworkLogon.ShareLogon? login = null;
        NetworkLogon.ShareLogon? admin = null;

        if (user.Length == 0)
        {
            items.Add(new DiagItem("凭据", DiagLevel.Warn,
                "没有填账户，将以当前登录账户连接",
                "工作组环境里，「本机账户」在 NAS 上不存在，通常直接被拒绝。\n" +
                "请填写 NAS 上的管理员账户，例如 NASBOX\\nasadm。"));
        }
        else if (profile.GetPassword() is null)
        {
            items.Add(new DiagItem("凭据", DiagLevel.Fail,
                $"{NetworkLogon.Describe(user, profile)} 没有保存密码",
                "在「连接」页重新输入密码并勾选「记住密码」。"));
        }
        else
        {
            items.Add(new DiagItem("凭据", DiagLevel.Info,
                $"将使用 {NetworkLogon.Describe(user, profile)}"));

            // Two shares, two questions. C$ answers "administrator?" and only
            // falls back to IPC$ when it refuses, to tell a wrong password
            // apart from an account that simply has no rights.
            (login, admin) = NetworkLogon.TestCredentials(profile);

            items.Add(login.Ok
                ? new DiagItem("账户验证", DiagLevel.Ok, login.Detail)
                : login.Code == -1
                    ? new DiagItem("账户验证", DiagLevel.Fail, login.Detail, "重新输入密码并勾选「记住密码」。")
                    : new DiagItem("账户验证", DiagLevel.Fail, login.Detail, CredentialFix));

            items.Add(admin.Ok
                ? new DiagItem("远程管理权限", DiagLevel.Ok, admin.Detail)
                : new DiagItem("远程管理权限", DiagLevel.Fail, admin.Detail, AdminRightsFix(user)));
        }

        // -------------------------------------------------- the real test
        WinnasSession? session = null;
        var connected = false;
        try
        {
            // verify:false — this method is itself the verification, and it
            // wants the raw exception rather than a wrapped one.
            session = WinnasSession.Open(profile, verify: false);
            var os = session.First(Cim.CimV2, "SELECT * FROM Win32_OperatingSystem");
            connected = os is not null;
            var caption = os is null ? "" : Cim.Str(os, "Caption");
            var version = os is null ? "" : Cim.Str(os, "Version");
            var name = os is null ? "" : Cim.Str(os, "CSName");

            items.Add(connected
                ? new DiagItem("WMI 查询", DiagLevel.Ok, $"{name} · {caption} {version}", "")
                : new DiagItem("WMI 查询", DiagLevel.Fail, "连上了但读不到 Win32_OperatingSystem"));
        }
        catch (Exception ex)
        {
            // Once credentials and remote admin both pass, "wrong password" is
            // no longer a live hypothesis. Keeping the generic 0x80070005
            // explanation here made the row contradict the two ticks above it —
            // and sent the user back to re-typing a password that already works.
            var detail = admin?.Ok == true
                ? "访问被拒绝（0x80070005），但凭据和远程管理权限都已通过 —— 不是密码问题，" +
                  "是 DCOM 授权或 Winmgmt 服务。"
                : OpResult.From(ex).Message;

            items.Add(new DiagItem("WMI 查询", DiagLevel.Fail, detail, WmiFix(admin?.Ok)));
        }

        if (session is not null && connected)
        {
            // -------------------------------------------------- permissions
            items.Add(CheckAdmin(session, profile));
            items.Add(CheckUacFiltering(session));
            items.Add(CheckRemoteControl(session));
            items.Add(CheckClock(session));
        }

        session?.Dispose();
        items.Add(Summary(items));
        return items;
    });

    /// <summary>
    /// One line that states the verdict instead of leaving the reader to scan
    /// nine rows. The first blocking item decides, because fixing anything
    /// below it is pointless until it is resolved.
    /// </summary>
    private static DiagItem Summary(List<DiagItem> items)
    {
        var first = items.FirstOrDefault(i => i.Level == DiagLevel.Fail);
        if (first is null)
        {
            var warn = items.FirstOrDefault(i => i.Level == DiagLevel.Warn);
            return warn is null
                ? new DiagItem("结论", DiagLevel.Ok, "全部检查通过，连接可用")
                : new DiagItem("结论", DiagLevel.Warn, $"可以使用，但「{warn.Name}」需要注意：{warn.Detail}");
        }

        return new DiagItem("结论", DiagLevel.Fail, $"卡在「{first.Name}」：{first.Detail}");
    }

    private const string CredentialFix =
        "这是登录凭据本身被拒绝，还轮不到权限问题。逐条核对：\n" +
        "· 口令区分大小写；\n" +
        "· 账户名是 NAS 上的本地账户，不是 NAS 的主机名；\n" +
        "· 若目标机加入了域，请把用户名写成 域\\账户；工作组则写 主机名\\账户；\n" +
        "· 先在 NAS 上本地登录一次该账户，确认能进。\n" +
        "不知道 NAS 上有哪些账户？在 NAS 上以管理员运行：net user / net localgroup Administrators";

    private static string AdminRightsFix(string user)
    {
        var name = user.Contains('\\') ? user[(user.LastIndexOf('\\') + 1)..] : user;
        return
            "密码是对的，但这个账户在 NAS 上没有远程管理权限。两种可能，按顺序排除：\n" +
            "① 不在管理员组——在被控端以管理员运行：\n" +
            $"   net localgroup Administrators \"{name}\" /add\n" +
            "② 在管理员组，但工作组机器默认把本地账户的远程连接降权：\n" +
            "   New-ItemProperty -Path HKLM:////SOFTWARE////Microsoft////Windows////CurrentVersion////Policies////System " +
            "-Name LocalAccountTokenFilterPolicy -Value 1 -PropertyType DWord -Force\n" +
            "改完注销并重新登录被控端（或重启），再点一次诊断。";
    }

    private static string WmiFix(bool? adminOk)
    {
        if (adminOk == false)
            return "管理权限还没通（见上面「远程管理权限」），WMI 一定会被拒绝——先解决那一条。";

        return
            "管理员权限已经通了，说明卡在 DCOM / WMI 这一层：\n" +
            "① 被控端放行 WMI 防火墙规则（135 通不代表 RPC 动态端口通）。\n" +
            "   用资源 ID 匹配，中文系统上也不会匹配不到：\n" +
            "   $g = @('@FirewallAPI.dll,-34251')\n" +
            "   Get-NetFirewallRule | Where-Object { $g -contains $_.Group } | Enable-NetFirewallRule\n" +
            "② 确认 WMI 服务在跑：Get-Service Winmgmt\n" +
            "③ 仍是拒绝访问，则是 DCOM 授权问题：在被控端运行 dcomcnfg → 组件服务 → 计算机 → 我的电脑 → " +
            "属性 → COM 安全 → 编辑限制，把该账户加入「本地访问/远程访问/本地启动/远程启动」；" +
            "或直接给 Administrators 组授权。\n" +
            "④ 工作组环境还要确认该账户没被「从网络访问此计算机」排除。\n" +
            "⑤ 以上都正常却仍被拒绝，先跑一次 --authprobe（SMB / DCOM 三种账户写法逐项实测），" +
            "它会直接指出哪条通道通、哪条不通，不用再靠猜。";
    }

    // -------------------------------------------------------------- probes

    private static List<string> Resolve(string host, out string error)
    {
        error = "";
        if (IPAddress.TryParse(host, out var ip)) return new List<string> { ip.ToString() };

        try
        {
            var task = Dns.GetHostAddressesAsync(host);
            if (!task.Wait(4000))
            {
                error = "DNS 超时";
                return new List<string>();
            }
            var v4 = task.Result.Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToList();
            var list = v4.Count > 0 ? v4 : task.Result.ToList();
            return list.Select(a => a.ToString()).ToList();
        }
        catch (Exception ex)
        {
            error = ex.InnerException?.Message ?? ex.Message;
            return new List<string>();
        }
    }

    private static bool Probe(string host, int port)
    {
        try
        {
            using var client = new TcpClient();
            var task = client.ConnectAsync(host, port);
            return task.Wait(2000) && client.Connected;
        }
        catch { return false; }
    }

    // ---------------------------------------------------------- deep checks

    private static DiagItem CheckAdmin(WinnasSession s, ConnectionProfile profile)
    {
        try
        {
            var admins = s.Query(Cim.CimV2,
                "SELECT * FROM Win32_Group WHERE LocalAccount=True AND SID='S-1-5-32-544'");
            if (admins.Count == 0)
                return new DiagItem("管理员组", DiagLevel.Info, "读不到本机管理员组，跳过核对");

            var members = s.Associated(Cim.CimV2, admins[0], "Win32_GroupUser", "Win32_UserAccount");
            if (members.Count == 0)
                return new DiagItem("管理员组", DiagLevel.Info, "读不到成员列表，跳过核对");

            var wanted = profile.UserName.Trim();
            var slash = wanted.LastIndexOf('\\');
            if (slash >= 0) wanted = wanted[(slash + 1)..];

            var found = members.Any(m => string.Equals(Cim.Str(m, "Name"), wanted, StringComparison.OrdinalIgnoreCase));

            if (found)
                return new DiagItem("管理员组", DiagLevel.Ok, $"{wanted} 在 Administrators 组内");

            var names = string.Join("、", members.Select(m => Cim.Str(m, "Name")).Take(6));
            return new DiagItem("管理员组", DiagLevel.Fail,
                $"Administrators 里没有找到 {wanted}（现有：{names}）",
                "WMI 远程操作要求账户在管理员组。在被控端以管理员运行：\n" +
                $"net localgroup Administrators \"{wanted}\" /add");
        }
        catch (Exception ex)
        {
            return new DiagItem("管理员组", DiagLevel.Info, "核对失败：" + OpResult.From(ex).Message);
        }
    }

    private static DiagItem CheckUacFiltering(WinnasSession s)
    {
        try
        {
            var args = new Dictionary<string, object>
            {
                ["hDefKey"] = Hklm,
                ["sSubKeyName"] = UacKey,
                ["sValueName"] = "LocalAccountTokenFilterPolicy",
            };

            var r = s.InvokeStatic(Cim.CimV2, "StdRegProv", "GetDWORDValue", args);
            var rc = r.ReturnCode();
            var value = r.OutString("uValue");

            if (rc == 0 && value == "1")
                return new DiagItem("UAC 远程限制", DiagLevel.Ok,
                    "LocalAccountTokenFilterPolicy=1，本地账户远程时保留完整管理员权限");

            return new DiagItem("UAC 远程限制", DiagLevel.Fail,
                rc == 0
                    ? $"LocalAccountTokenFilterPolicy={value}，本地账户的远程连接被剥离管理员权限"
                    : "LocalAccountTokenFilterPolicy 未设置（等同 0），本地账户的远程连接被剥离管理员权限",
                "这是工作组环境里「凭据明明是对的却报拒绝访问」的最常见原因。\n" +
                "在被控端以管理员运行 PowerShell：\n" +
                "New-ItemProperty -Path HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\System " +
                "-Name LocalAccountTokenFilterPolicy -Value 1 -PropertyType DWord -Force\n" +
                "（改用域账户连接则不需要这一项）");
        }
        catch (Exception ex)
        {
            return new DiagItem("UAC 远程限制", DiagLevel.Info, "读不到：" + OpResult.From(ex).Message);
        }
    }

    private static DiagItem CheckRemoteControl(WinnasSession s)
    {
        try
        {
            var procs = s.Query(Cim.CimV2, "SELECT ProcessId FROM Win32_Process").Count;
            var svcs = s.Query(Cim.CimV2, "SELECT Name FROM Win32_Service").Count;
            return new DiagItem("远控前置条件", DiagLevel.Ok,
                $"可枚举 {procs} 个进程、{svcs} 个服务，远控页的按钮可用");
        }
        catch (Exception ex)
        {
            return new DiagItem("远控前置条件", DiagLevel.Fail,
                "进程/服务枚举被拒绝：" + OpResult.From(ex).Message,
                "远控需要管理员权限；先解决上面两项。");
        }
    }

    /// <summary>
    /// A clock that is minutes or days off does not break the connection, so
    /// this is a Warn rather than a Fail — but it poisons every timestamp the
    /// user will ever read from that machine (security log, scheduled runs,
    /// backup history). This NAS once ran 22 days slow, which is why the check
    /// exists at all.
    /// </summary>
    private static DiagItem CheckClock(WinnasSession s)
    {
        var state = TimeSync.Inspect(s);
        if (!state.Ok)
            return new DiagItem("被控端时钟", DiagLevel.Info, state.Detail);

        var tolerance = TimeSync.ClampTolerance(AppState.Settings.TimeToleranceSeconds);
        if (state.Drifted(tolerance))
            return new DiagItem("被控端时钟", DiagLevel.Warn,
                $"被控端{state.DriftText}（本机 {state.LocalText} / 被控端 {state.TargetText}）。" +
                "偏差会让安全日志、计划任务和备份记录的时间戳失去意义。",
                "在「同步」页点「立即校准」，或勾选「连接成功后自动检查并校准」让程序每次连接时自动对齐。");

        return new DiagItem("被控端时钟", DiagLevel.Ok,
            $"偏差 {state.DriftText}，在容差 {tolerance} 秒内");
    }


}
