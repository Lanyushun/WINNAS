using System.Management;
using Microsoft.Win32.SafeHandles;

namespace Winnas.Console.Infrastructure;

/// <summary>
/// A live WMI connection to a target machine, local or remote.
///
/// This uses the classic <c>System.Management</c> stack, not
/// <c>Microsoft.Management.Infrastructure</c>. That is not a taste decision:
/// MI was measured against this NAS and lost. Same account, same password,
/// same instant —
///
///   MI  + DComSessionOptions + impersonated token  → 0x80070005 拒绝访问
///   MI  + WSManSessionOptions + CimCredential      → "无法创建 WinRM 会话"
///   System.Management + ConnectionOptions          → 成功，三种账户写法全通
///
/// MI's DCOM transport has no way to carry credentials, so it can only borrow
/// the calling thread's token, and a borrowed token from a workgroup desktop
/// is what the target refuses. System.Management passes the account through
/// ConnectionOptions, which is what <c>Get-WmiObject -Credential</c> does and
/// what has worked against workgroup machines since Windows 2000.
///
/// The impersonation token is still created, but only for the SMB side — the
/// file, backup and remote-shell pages talk to shares, and those do go by the
/// thread's network identity.
/// </summary>
public sealed class WinnasSession : IDisposable
{
    public ConnectionProfile Profile { get; }
    public bool IsRemote { get; }
    public TtlCache Cache { get; } = new();

    /// <summary>Non-null when the SMB side acts as a specific account.</summary>
    private readonly SafeAccessTokenHandle? _token;

    /// <summary>Which account the target sees, for display in diagnostics.</summary>
    public string IdentityText { get; }

    /// <summary>
    /// The target's own computer name, read during the connect probe. Showing
    /// the name the target reports itself is the only way to be sure the
    /// numbers on screen are not coming from this desktop.
    /// </summary>
    public string MachineName { get; private set; } = "";

    private readonly ConnectionOptions? _options;
    private readonly string _host = "";
    private readonly Dictionary<string, ManagementScope> _scopes = new(StringComparer.OrdinalIgnoreCase);

    private WinnasSession(ConnectionProfile profile, bool isRemote,
        SafeAccessTokenHandle? token, string identity, ConnectionOptions? options, string host)
    {
        Profile = profile;
        IsRemote = isRemote;
        _token = token;
        IdentityText = identity;
        _options = options;
        _host = host;
    }

    public string Label => IsRemote ? $"{Profile.Name} ({Profile.Target})" : $"本机 · {Environment.MachineName}";

    /// <summary>
    /// Opens a session and — unless <paramref name="verify"/> is false — proves
    /// it actually works by running one real query before handing it back.
    ///
    /// This exists because the WMI stack connects lazily: creating the scope
    /// succeeds even when the target will reject every single call. That made
    /// the window say "已连接 192.168.1.10" while every page was empty. A
    /// connection that cannot answer one question is not a connection.
    /// </summary>
    public static WinnasSession Open(ConnectionProfile profile, bool verify = true)
    {
        var session = Create(profile);
        if (!verify) return session;

        try
        {
            session.Probe();
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private static WinnasSession Create(ConnectionProfile profile)
    {
        if (profile.IsLocal)
            return new WinnasSession(profile, false, null,
                $@"{Environment.UserDomainName}\{Environment.UserName}", null, ".");

        var computer = profile.Computer.Trim();
        var user = profile.UserName.Trim();

        // The impersonation token is only for SMB work (see the class comment).
        var token = NetworkLogon.CreateToken(profile, out var logonError);

        var identity = token is null
            ? $@"{Environment.UserDomainName}\{Environment.UserName}（当前登录账户）"
            : NetworkLogon.Describe(user, profile);

        ConnectionOptions? options = null;
        if (user.Length > 0)
        {
            if (logonError.Length > 0) throw new UnauthorizedAccessException(logonError);

            var pwd = profile.GetPassword();
            if (pwd is null || pwd.Length == 0)
                throw new UnauthorizedAccessException("这条连接填了用户名但没有保存密码。请在「连接」页重新输入密码并勾选「记住密码」。");

            var plain = SecretVault.ConsumeToPlain(pwd);
            pwd.Dispose();

            try
            {
                var (name, domain) = NetworkLogon.Split(user, profile);
                options = new ConnectionOptions
                {
                    Username = domain.Length == 0 ? name : $@"{domain}\{name}",
                    Password = plain,
                    Authentication = AuthenticationLevel.PacketPrivacy,
                    Impersonation = ImpersonationLevel.Impersonate,
                    EnablePrivileges = true,
                    Timeout = TimeSpan.FromSeconds(30),
                };
            }
            finally { plain = string.Empty; }
        }

        return new WinnasSession(profile, true, token, identity, options, computer);
    }

    /// <summary>
    /// Runs an operation as the account this session was opened with. A no-op
    /// for local sessions and for connections that use the current logon.
    ///
    /// WMI does not need this any more — ConnectionOptions carries the account.
    /// The file, backup and remote-shell pages do: they open UNC paths as the
    /// thread identity, and thread impersonation does not survive a move to a
    /// worker thread, so the scope has to be re-entered around each call.
    /// </summary>
    public IDisposable Impersonate() =>
        _token is null ? NoopScope.Instance : NetworkLogon.Impersonate(_token);

    private sealed class NoopScope : IDisposable
    {
        public static readonly NoopScope Instance = new();
        public void Dispose() { }
    }

    private ManagementScope ScopeFor(string ns)
    {
        if (_scopes.TryGetValue(ns, out var existing)) return existing;

        var path = IsRemote ? $@"\\{_host}\{ns}" : $@"\\.\{ns}";
        var scope = new ManagementScope(path, _options);
        scope.Connect();
        _scopes[ns] = scope;
        return scope;
    }

    /// <summary>
    /// One cheap round trip that a working connection can always answer.
    /// Win32_ComputerSystem is readable by any authenticated user, so a failure
    /// here really is a failure and not a permission nuance.
    /// </summary>
    private void Probe()
    {
        var rows = Query(Cim.CimV2, "SELECT Name FROM Win32_ComputerSystem");
        var name = rows.Count > 0 ? Cim.Str(rows[0], "Name") : "";

        if (name.Length == 0)
            throw new InvalidOperationException(
                $"能连到 {Profile.Target} 的 WMI 服务，但取不到计算机名，目标机的 WMI 可能已损坏。");

        MachineName = name;
    }

    // ------------------------------------------------------------- queries

    public List<ManagementBaseObject> Query(string ns, string wql)
    {
        var list = new List<ManagementBaseObject>();
        using var searcher = new ManagementObjectSearcher(ScopeFor(ns), new ObjectQuery(wql));
        foreach (ManagementBaseObject o in searcher.Get()) list.Add(o);
        return list;
    }

    public ManagementBaseObject? First(string ns, string wql) => Query(ns, wql).FirstOrDefault();

    /// <summary>
    /// Follow an association. This is the only way to reach
    /// MSFT_StorageReliabilityCounter: the class itself returns zero rows on a
    /// direct query, it is only reachable via
    /// MSFT_PhysicalDiskToStorageReliabilityCounter.
    /// </summary>
    public List<ManagementBaseObject> Associated(string ns, ManagementBaseObject source,
        string assocClass, string resultClass)
    {
        var list = new List<ManagementBaseObject>();

        // Instances that came out of a method's out-parameters carry no path,
        // so there is nothing to follow from them.
        if (source is not ManagementObject src) return list;

        try
        {
            using var searcher = new ManagementObjectSearcher(ScopeFor(ns),
                new RelatedObjectQuery(src.Path.Path, resultClass, assocClass, null, null, null, null, false));
            foreach (ManagementBaseObject o in searcher.Get()) list.Add(o);
        }
        catch { /* counters are optional; absence is not an error */ }
        return list;
    }

    public ManagementBaseObject InvokeStatic(string ns, string className, string method,
        IDictionary<string, object>? args = null)
    {
        using var cls = new ManagementClass(ScopeFor(ns), new ManagementPath(className), null);
        using var inParams = cls.GetMethodParameters(method);
        Fill(inParams, args);
        return cls.InvokeMethod(method, inParams, null);
    }

    public ManagementBaseObject InvokeInstance(ManagementBaseObject instance, string method,
        IDictionary<string, object>? args = null)
    {
        var obj = AsObject(instance);
        using var inParams = obj.GetMethodParameters(method);
        Fill(inParams, args);
        return obj.InvokeMethod(method, inParams, null);
    }

    public void DeleteInstance(ManagementBaseObject instance) => AsObject(instance).Delete();

    /// <summary>
    /// Instances handed back by a query are ManagementObjects already and carry
    /// their absolute path. Objects that came out of a method's out-parameters
    /// do not, and an instance method cannot be invoked without one — so those
    /// callers have to re-query the object first.
    /// </summary>
    private static ManagementObject AsObject(ManagementBaseObject instance) =>
        instance as ManagementObject
        ?? throw new InvalidOperationException(
            "这个 WMI 对象没有实例路径，无法直接调用实例方法。先按主键查一次再调用。");

    private static void Fill(ManagementBaseObject inParams, IDictionary<string, object>? args)
    {
        if (args is null) return;
        foreach (var kv in args)
        {
            // Assigning a property the method does not declare throws, and a
            // silent typo would look like a failed repair.
            if (inParams.Properties[kv.Key] is null)
                throw new ArgumentException($"方法没有名为 {kv.Key} 的参数。");
            inParams[kv.Key] = kv.Value;
        }
    }

    public void Dispose()
    {
        _scopes.Clear();
        try { _token?.Dispose(); } catch { /* already closed */ }
    }
}

public static class CimResultExtensions
{
    public static uint ReturnCode(this ManagementBaseObject r)
    {
        var v = r.Properties["ReturnValue"]?.Value;
        if (v is null) return 0;
        if (v is uint u) return u;
        try { return Convert.ToUInt32(v); } catch { return uint.MaxValue; }
    }

    public static List<ManagementBaseObject> Instances(this ManagementBaseObject r, string outName)
    {
        var result = new List<ManagementBaseObject>();
        var v = r.Properties[outName]?.Value;
        if (v is ManagementBaseObject[] arr) result.AddRange(arr);
        else if (v is ManagementBaseObject one) result.Add(one);
        return result;
    }

    public static string? OutString(this ManagementBaseObject r, string outName) =>
        r.Properties[outName]?.Value?.ToString();
}
