using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Winnas.Console.Infrastructure;

/// <summary>
/// Turns a user name + password into an impersonation token.
///
/// This exists because of a hard limitation in the CIM client library:
/// <c>DComSessionOptions</c> has no way to carry credentials. DCOM always
/// authenticates with the token of the calling thread, so a remote connection
/// made from a workgroup desktop can only ever use "whoever is logged on here".
/// That is why typing an account into the connection dialog used to change
/// nothing and the target answered "access denied".
///
/// LOGON32_LOGON_NEW_CREDENTIALS solves it without touching the rest of the
/// process: it produces a token whose *outbound network* identity is the
/// supplied account, which is what <c>net use \\host\ipc$ /user:...</c> does
/// internally. Unlike an interactive logon it does not require the account to
/// have "log on locally" rights on this machine, and it is not validated until
/// the first network call — so a wrong password surfaces at the CIM call, not
/// here. That is why the diagnostics check group membership separately.
/// </summary>
public static class NetworkLogon
{
    private const int Logon32LogonNewCredentials = 9;
    private const int Logon32ProviderWinnt50 = 3;

    private const int ErrorLogonFailure = 1326;
    private const int ErrorLogonTypeNotGranted = 1385;
    private const int ErrorAccountRestriction = 1327;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LogonUser(
        string user, string? domain, string password,
        int logonType, int logonProvider, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ImpersonateLoggedOnUser(IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool RevertToSelf();

    /// <summary>
    /// Impersonation depth on the current thread.
    ///
    /// RevertToSelf always returns to the *process* token, not to whatever the
    /// thread carried before, so a nested scope would silently drop the outer
    /// one. Counting here makes nesting safe: only the outermost pair touches
    /// the thread token.
    /// </summary>
    [ThreadStatic] private static int _depth;

    /// <summary>Acts as <paramref name="token"/> until the returned scope is disposed.</summary>
    public static IDisposable Impersonate(SafeAccessTokenHandle token)
    {
        if (token.IsInvalid) return Noop.Instance;

        if (_depth > 0)
        {
            _depth++;
            return new Scope(false);
        }

        if (!ImpersonateLoggedOnUser(token.DangerousGetHandle())) return Noop.Instance;

        _depth = 1;
        return new Scope(true);
    }

    private sealed class Scope : IDisposable
    {
        private readonly bool _owns;
        public Scope(bool owns) => _owns = owns;

        public void Dispose()
        {
            if (_depth > 0) _depth--;
            if (_owns && _depth == 0) RevertToSelf();
        }
    }

    private sealed class Noop : IDisposable
    {
        public static readonly Noop Instance = new();
        public void Dispose() { }
    }

    /// <summary>
    /// Token for <paramref name="profile"/>, or null when the profile uses the
    /// current Windows logon (local sessions, DCOM without an account).
    /// </summary>
    public static SafeAccessTokenHandle? CreateToken(ConnectionProfile profile, out string error)
    {
        error = "";

        var user = profile.UserName.Trim();
        if (user.Length == 0) return null;              // current logon on purpose

        var pwd = profile.GetPassword();
        if (pwd is null || pwd.Length == 0)
        {
            error = "这条连接填了用户名但没有保存密码。请在「连接」页重新输入密码并勾选「记住密码」。";
            return null;
        }

        var plain = SecretVault.ConsumeToPlain(pwd);
        var (name, domain) = Split(user, profile);

        try
        {
            if (LogonUser(name, domain, plain, Logon32LogonNewCredentials, Logon32ProviderWinnt50, out var token))
                return token;

            var code = Marshal.GetLastWin32Error();
            error = code switch
            {
                ErrorLogonFailure => $"账户或密码不正确：{domain}\\{name}",
                ErrorLogonTypeNotGranted => $"账户 {domain}\\{name} 未被授予「从网络访问此计算机」权限。",
                ErrorAccountRestriction => $"账户 {domain}\\{name} 受登录时段或工作站限制。",
                _ => $"无法为 {domain}\\{name} 建立登录令牌（Win32 错误 {code}）：{new System.ComponentModel.Win32Exception(code).Message}",
            };

            // No token means "connect as the current user", which is exactly the
            // silent downgrade that produced the confusing access-denied error.
            return null;
        }
        finally
        {
            plain = string.Empty;
            pwd.Dispose();
        }
    }

    /// <summary>
    /// NTLM needs a domain field even for a local account, and for those the
    /// domain *is* the machine's own name. Users type an IP address in the
    /// dialog, which never authenticates as a domain, so translate it back to
    /// the NetBIOS name when reverse DNS can supply one.
    /// </summary>
    public static (string User, string Domain) Split(string user, ConnectionProfile profile)
    {
        // "DOMAIN\user" or "user@domain" — the user already decided.
        var slash = user.IndexOf('\\');
        if (slash > 0 && slash < user.Length - 1)
            return (user[(slash + 1)..], user[..slash]);

        var at = user.IndexOf('@');
        if (at > 0 && at < user.Length - 1)
            return (user[..at], user[(at + 1)..]);

        var target = profile.Computer.Trim();
        var domain = !string.IsNullOrWhiteSpace(profile.Domain) ? profile.Domain.Trim() : MachineDomain(target);
        return (user, domain);
    }

    /// <summary>
    /// Machine name to use as the NTLM domain for a local account.
    ///
    /// Returns an empty string when the name cannot be discovered — an IP
    /// address must never be used as the domain (the original version chopped
    /// "192.168.137.50" at the first dot and sent "192"). An empty domain makes
    /// NTLM fall back to the target's own SAM, which is what a workgroup
    /// machine expects.
    /// </summary>
    public static string MachineDomain(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return "";
        if (!IPAddress.TryParse(host, out _)) return host;

        try
        {
            // Reverse DNS can be slow on a LAN with no DNS server; do not let a
            // missing answer hold up the connect button.
            var lookup = Dns.GetHostEntryAsync(host);
            if (lookup.Wait(2000))
            {
                var name = lookup.Result.HostName;
                var dot = name.IndexOf('.');
                var shortName = dot > 0 ? name[..dot] : name;
                // Reverse lookup that just echoes the address tells us nothing.
                if (shortName.Length > 0 && !IPAddress.TryParse(shortName, out _)) return shortName;
            }
        }
        catch { /* no PTR record is normal on a workgroup LAN */ }

        return "";
    }

    /// <summary>How the account should read on screen.</summary>
    public static string Describe(string user, ConnectionProfile profile)
    {
        var (name, domain) = Split(user, profile);
        return domain.Length == 0 ? $"{name}（目标机本地账户）" : $"{domain}\\{name}";
    }

    // --------------------------------------------------------- credential test

    private const int ResourceTypeDisk = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetResource
    {
        public int Scope;
        public int Type;
        public int DisplayType;
        public int Usage;
        public string? LocalName;
        public string? RemoteName;
        public string? Comment;
        public string? Provider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2(ref NetResource resource, string? password,
        string? username, int flags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetCancelConnection2(string name, int flags, bool force);

    /// <summary>One SMB logon attempt against one share.</summary>
    public sealed record ShareLogon(string Share, int Code, string Detail)
    {
        public bool Ok => Code == 0;
    }

    /// <summary>
    /// Probes two shares, because they answer two different questions:
    ///
    ///   C$   — "is this account an administrator on the target?" A normal user
    ///          cannot open an administrative share at all, so a success here
    ///          also proves the UAC remote-token filter is not stripping the
    ///          admin rights (LocalAccountTokenFilterPolicy=1 or a domain acct).
    ///   IPC$ — "is the password right?" IPC$ needs only authentication, not
    ///          privileges, so it still answers when C$ refuses.
    ///
    /// Testing only IPC$ was the earlier mistake: it reported "credentials are
    /// valid" for an account that had no rights at all, which sent the user
    /// looking in the wrong place when WMI still said access denied.
    /// </summary>
    public static (ShareLogon Login, ShareLogon Admin) TestCredentials(ConnectionProfile profile)
    {
        var user = profile.UserName.Trim();
        if (user.Length == 0)
        {
            var note = new ShareLogon("-", 0, "未指定账户，使用当前登录身份");
            return (note, note);
        }

        var pwd = profile.GetPassword();
        if (pwd is null || pwd.Length == 0)
        {
            var miss = new ShareLogon("-", -1, "没有保存密码");
            return (miss, miss);
        }

        var plain = SecretVault.ConsumeToPlain(pwd);
        pwd.Dispose();

        var (name, domain) = Split(user, profile);
        var account = domain.Length == 0 ? name : $"{domain}\\{name}";
        var host = profile.Computer.Trim();

        try
        {
            // The administrative share answers both questions at once, so try
            // it first and only fall back to IPC$ to explain a refusal.
            var admin = TryShare($@"\\{host}\C$", account, plain);
            if (admin.Ok) return (admin with { Detail = $"凭据有效：{account}" }, admin);

            var login = TryShare($@"\\{host}\IPC$", account, plain);
            return (login, admin);
        }
        finally { plain = string.Empty; }
    }

    /// <summary>
    /// Opens one SMB session with an explicit account string and leaves it
    /// open. The account form is passed through untouched, which is what makes
    /// this useful for telling "nasadmin" from "ShawnNAS\nasadmin" from
    /// ".\nasadmin" — the three forms the target logs differently.
    /// </summary>
    public static int ConnectShare(string unc, string? account, string? password)
    {
        var resource = new NetResource { Type = ResourceTypeDisk, RemoteName = unc };
        return WNetAddConnection2(ref resource, password, account, 0);
    }

    public static void DisconnectShare(string unc) => WNetCancelConnection2(unc, 0, true);

    /// <summary>One share attempt with the account string written as given.</summary>
    public static ShareLogon ProbeShare(string host, string share, string account, string password) =>
        TryShare($@"\\{host}\{share}", account, password);

    private static ShareLogon TryShare(string unc, string account, string password)
    {
        var resource = new NetResource { Type = ResourceTypeDisk, RemoteName = unc };
        var code = WNetAddConnection2(ref resource, password, account, 0);

        // Remove it again: the real work uses impersonation, and a leftover
        // session would silently change how this process authenticates later.
        if (code == 0) WNetCancelConnection2(unc, 0, true);

        var share = unc[(unc.LastIndexOf('\\') + 1)..];
        return new ShareLogon(share, code, Explain(code, account, share));
    }

    private static string Explain(int code, string account, string share) => code switch
    {
        0 when share == "C$" => $"{account} 具备远程管理员权限（可以打开管理共享 C$）",
        0 => $"凭据有效：{account} 可以登录目标机",
        86 => $"密码不正确：{account}",
        1326 => $"账户不存在或密码不正确：{account}",
        5 => $"{account} 被拒绝访问 {share}（账户存在，但没有这项权限）",
        1327 => $"账户受限：{account} 不允许空密码或受登录时段限制",
        1219 => $"这台电脑已用另一个账户连接过该主机，凭据冲突（现有会话在拦路）",
        53 or 67 or 64 => $"找不到网络路径 {share}，确认目标机在线且共享服务已启动",
        1311 or 1272 => "目标或域当前无法处理登录请求",
        _ => $"SMB 登录失败（错误 {code}）：{new System.ComponentModel.Win32Exception(code).Message}",
    };
}
