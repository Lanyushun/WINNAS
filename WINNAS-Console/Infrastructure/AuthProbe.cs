using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Winnas.Console.Infrastructure;

/// <summary>
/// "WINNAS.Console.exe --authprobe &lt;连接&gt;" — works out which account form
/// the target actually accepts, using the password already stored in the
/// profile.
///
/// Written after a round of guessing went nowhere. NTLM carries a domain field
/// even for a local account, and the three spellings "nasadmin",
/// ".\nasadmin" and "NASBOX\nasadmin" are *not* interchangeable: the target
/// logs them with domain "" / "." / "NASBOX", and only the third resolves
/// against its own SAM with any predictability. Which one works cannot be
/// derived from the outside — so probe all of them and stop guessing.
/// </summary>
public static class AuthProbe
{
    public static int Run(string which)
    {
        var profile = ConnectionDiagnosticsCli.Pick(which);
        if (profile is null) { Say("找不到匹配的连接。"); return 2; }

        var host = profile.Computer.Trim();
        var user = profile.UserName.Trim();

        Say($"目标：{host}    账户：{user}");
        Say($"当前传输：{profile.TransportText}    域字段：{(profile.Domain.Trim().Length == 0 ? "(空)" : profile.Domain)}");
        Say(new string('-', 66));

        var pwd = profile.GetPassword();
        if (pwd is null || pwd.Length == 0)
        {
            Say("这条连接没有保存密码（DPAPI 里读不到），无法探测。");
            Say("先在「连接」页把密码填上并勾选「记住密码」。");
            return 2;
        }

        var plain = SecretVault.ConsumeToPlain(pwd);
        pwd.Dispose();
        Say($"密码已从 DPAPI 读出：{plain.Length} 个字符（不显示内容）");
        Say(new string('-', 66));

        try
        {
            return Probe(profile, host, user, plain);
        }
        finally { plain = string.Empty; }
    }

    private static int Probe(ConnectionProfile profile, string host, string user, string plain)
    {
        // ---------------------------------------------------------- 1. SMB
        Say("【1】SMB 登录：目标接受哪种账户写法");
        var forms = new[] { user, $@".\{user}" };
        string? smbOk = null;
        foreach (var account in forms)
        {
            var r = NetworkLogon.ProbeShare(host, "IPC$", account, plain);
            Say($"    {account,-24} → {(r.Ok ? "成功" : r.Detail)}");
            if (r.Ok && smbOk is null) smbOk = account;
        }
        Say("");

        // ------------------------------------- 2. what the target calls itself
        Say("【2】问目标机自己的计算机名（已认证的 SMB 会话 + wkssvc）");
        var name = DiscoverName(host, smbOk, plain, out var how);
        Say(name.Length > 0
            ? $"    {how} → {name}"
            : $"    取不到（{how}）");
        Say("");

        // ------------------------------------------------ 3. which domain form
        var candidates = new List<string>();
        void Add(string d)
        {
            d = d.Trim();
            if (candidates.Any(c => string.Equals(c, d, StringComparison.OrdinalIgnoreCase))) return;
            candidates.Add(d);
        }

        Add(profile.Domain.Trim());
        Add("");
        Add(".");
        Add(name);
        var reverse = NetworkLogon.MachineDomain(host);
        if (reverse.Length > 0) Add(reverse);
        if (!System.Net.IPAddress.TryParse(host, out _)) Add(host);

        if (name.Length == 0)
        {
            // Whatever the profile is configured with has to be reported too,
            // otherwise a correct manual value looks like a failure.
            Say("    提示：取不到目标机名，下面的结果只覆盖了有限的几种写法。");
            Say("");
        }

        Say("【3】WMI over DCOM（控制台实际使用的通道）");
        var dcomWorked = "";
        foreach (var d in candidates)
        {
            var label = d.Length == 0 ? "(空)" : d;
            var r = TryDcom(profile, d);
            Say($"    域={label,-14} → {r}");
            if (r.StartsWith("成功") && dcomWorked.Length == 0) dcomWorked = d;
        }
        Say("");

        Say("【4】Get-WmiObject 交叉验证（PowerShell 自己的 ConnectionOptions 写法）");
        var legacyWorked = "";
        foreach (var d in candidates)
        {
            var label = d.Length == 0 ? "(空)" : d;
            var r = TryLegacyWmi(host, d, user, plain);
            Say($"    域={label,-14} → {r}");
            if (r.StartsWith("成功") && legacyWorked.Length == 0) legacyWorked = d;
        }
        Say("");

        // ------------------------------------------------------- 5. conclusion
        Say(new string('-', 66));
        if (dcomWorked.Length > 0)
        {
            Say("结论：WMI 通道可用。");
            Say(dcomWorked.Length == 0
                ? "      域字段留空即可。"
                : $"      域字段填：{dcomWorked}");
            Say("      按这个改完连接页，再跑 --ping 验证一次。");
            return 0;
        }

        Say("结论：账户写法全部被拒，但 SMB 能登录。");
        Say("      问题在目标机的 DCOM / WMI 授权层，不在密码，也不在账户写法。");
        Say("      下一步：dcomcnfg 放行该账户，或改用 SMB 通道（--exec）。");
        return 1;
    }

    /// <summary>
    /// Asks the target for its own NetBIOS name over an authenticated SMB
    /// session. wkssvc (NetWkstaGetInfo) is readable by any authenticated
    /// user; srvsvc (NetServerGetInfo) often needs admin, so it is only a
    /// fallback. Knowing this name matters because it is the only correct
    /// value for the NTLM domain of a local account.
    /// </summary>
    private static string DiscoverName(string host, string? account, string plain, out string how)
    {
        how = "未连接";
        var unc = $@"\\{host}\IPC$";
        var code = NetworkLogon.ConnectShare(unc, account, plain);
        if (code != 0)
        {
            how = $"IPC$ 连不上（{new Win32Exception(code).Message}）";
            return "";
        }

        try
        {
            how = "NetWkstaGetInfo";
            var n = WkstaName($@"\\{host}");
            if (n.Length > 0) return n;

            how = "NetServerGetInfo";
            n = ServerName($@"\\{host}");
            if (n.Length > 0) return n;

            how = "wkssvc/srvsvc 都拒绝（权限）";
            return "";
        }
        finally { NetworkLogon.DisconnectShare(unc); }
    }

    /// <summary>
    /// The pre-MI path: <c>Get-WmiObject</c> is System.Management, which sets up
    /// COM security per connection through ConnectionOptions and has supported
    /// explicit credentials from a workgroup machine since Windows 2000.
    /// MI's DCOM transport can only borrow the calling thread's token, so if
    /// this one works the whole remote layer should move here.
    /// </summary>
    private static string TryLegacyWmi(string host, string domain, string user, string plain)
    {
        var account = domain.Length == 0
            ? user
            : domain == "." ? $@".\{user}" : $@"{domain}\{user}";

        var script = string.Join("\n",
            "$ErrorActionPreference = 'Stop'",
            "try {",
            "  $s = ConvertTo-SecureString $env:WN_PWD -AsPlainText -Force",
            "  $c = New-Object System.Management.Automation.PSCredential($env:WN_ACCT, $s)",
            "  $o = Get-WmiObject -Class Win32_ComputerSystem -ComputerName $env:WN_HOST -Credential $c",
            "  'OK ' + $o.Name",
            "} catch { 'ERR ' + $_.Exception.Message }");

        var file = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"winnas-probe-{Guid.NewGuid():N}.ps1");

        try
        {
            // Windows PowerShell 5.1 reads a BOM-less UTF-8 file as GBK.
            System.IO.File.WriteAllText(file, script, new UTF8Encoding(true));

            var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe",
                $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{file}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.Environment["WN_ACCT"] = account;
            psi.Environment["WN_PWD"] = plain;
            psi.Environment["WN_HOST"] = host;

            using var p = System.Diagnostics.Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(60000))
            {
                try { p.Kill(true); } catch { }
                return "失败 · 超时 60 秒";
            }

            var line = stdout.Trim();
            if (line.StartsWith("OK"))
                return $"成功 · 目标机名 {line[2..].Trim()}";

            var msg = line.Length > 0 ? line : stderr.Trim();
            return "失败 · " + (msg.Length > 0 ? msg.Split('\n')[0].Trim() : $"退出码 {p.ExitCode}");
        }
        catch (Exception ex) { return "失败 · " + ex.Message; }
        finally { try { System.IO.File.Delete(file); } catch { } }
    }

    private static string TryDcom(ConnectionProfile profile, string domain)
    {
        var clone = profile.Clone();
        clone.Domain = domain;
        clone.Transport = TransportKind.DCom;

        try
        {
            using var session = WinnasSession.Open(clone);
            return $"成功 · 目标机名 {session.MachineName}";
        }
        catch (Exception ex) { return "失败 · " + Flat(ex); }
    }

    /// <summary>
    /// One line for a stack of MI wrappers. "拒绝访问" on its own says nothing;
    /// the HRESULT underneath is what identifies the layer that refused.
    /// </summary>
    private static string Flat(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e is not null && parts.Count < 3; e = e.InnerException)
            parts.Add($"[0x{e.HResult:X8}] {e.Message.Trim()}");
        return string.Join(" ← ", parts);
    }

    // ------------------------------------------------------------ netapi32

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetWkstaGetInfo(string? servername, int level, out IntPtr buffer);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetServerGetInfo(string? servername, int level, out IntPtr buffer);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WkstaInfo100
    {
        public int PlatformId;
        public IntPtr ComputerName;
        public IntPtr LangGroup;
        public int VerMajor;
        public int VerMinor;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ServerInfo101
    {
        public int PlatformId;
        public IntPtr Name;
        public int VerMajor;
        public int VerMinor;
        public int ServerType;
        public IntPtr Comment;
    }

    private static string WkstaName(string server)
    {
        var code = NetWkstaGetInfo(server, 100, out var buf);
        if (code != 0 || buf == IntPtr.Zero) return "";
        try
        {
            var info = Marshal.PtrToStructure<WkstaInfo100>(buf);
            return Marshal.PtrToStringUni(info.ComputerName)?.Trim() ?? "";
        }
        finally { NetApiBufferFree(buf); }
    }

    private static string ServerName(string server)
    {
        var code = NetServerGetInfo(server, 101, out var buf);
        if (code != 0 || buf == IntPtr.Zero) return "";
        try
        {
            var info = Marshal.PtrToStructure<ServerInfo101>(buf);
            return Marshal.PtrToStringUni(info.Name)?.Trim() ?? "";
        }
        finally { NetApiBufferFree(buf); }
    }

    private static void Say(string line)
    {
        try
        {
            System.Console.OutputEncoding = Encoding.UTF8;
            System.Console.WriteLine(line);
        }
        catch { /* headless */ }
    }
}
