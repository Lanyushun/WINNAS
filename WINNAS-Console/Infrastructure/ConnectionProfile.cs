using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Winnas.Console.Infrastructure;

public enum TransportKind
{
    Local,
    DCom,
    WSMan,
}

public sealed class ConnectionProfile
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("name")]
    public string Name { get; set; } = "本机";

    [JsonPropertyName("computer")]
    public string Computer { get; set; } = "";

    [JsonPropertyName("transport")]
    public TransportKind Transport { get; set; } = TransportKind.Local;

    [JsonPropertyName("user")]
    public string UserName { get; set; } = "";

    [JsonPropertyName("domain")]
    public string Domain { get; set; } = "";

    /// <summary>DPAPI-protected password, or null when not stored.</summary>
    [JsonPropertyName("pwd")]
    public string? ProtectedPassword { get; set; }

    /// <summary>Hardware address used by the Wake-on-LAN button on the remote-control page.</summary>
    [JsonPropertyName("mac")]
    public string MacAddress { get; set; } = "";

    [JsonIgnore]
    public bool IsLocal => Transport == TransportKind.Local
                           || string.IsNullOrWhiteSpace(Computer)
                           || Computer.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                           || Computer == "127.0.0.1"
                           || Computer.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Declared as a remote connection but with no address. Treating it as
    /// "local" would silently point the whole console at the desktop while the
    /// user believes they are looking at the NAS.
    /// </summary>
    [JsonIgnore]
    public bool IsMisconfigured => Transport != TransportKind.Local && string.IsNullOrWhiteSpace(Computer);

    [JsonIgnore]
    public string Target => IsMisconfigured
        ? "未填写主机名"
        : IsLocal ? Environment.MachineName : Computer.Trim();

    [JsonIgnore]
    public string TransportText => Transport switch
    {
        TransportKind.Local => "本机",
        // WSMan is a leftover from the WinRM era. The remote layer is WMI over
        // DCOM for every remote profile now, so an old WinRM setting is shown —
        // and connected — as what it actually is.
        TransportKind.DCom or TransportKind.WSMan => "远程 DCOM",
        _ => "本机",
    };

    public void SetPassword(SecureString? pwd)
    {
        if (pwd is null || pwd.Length == 0)
        {
            ProtectedPassword = null;
            return;
        }
        ProtectedPassword = SecretVault.Protect(pwd);
    }

    public SecureString? GetPassword()
    {
        if (string.IsNullOrEmpty(ProtectedPassword)) return null;
        try { return SecretVault.Unprotect(ProtectedPassword); }
        catch { return null; }
    }

    public ConnectionProfile Clone() => new()
    {
        Id = Id,
        Name = Name,
        Computer = Computer,
        Transport = Transport,
        UserName = UserName,
        Domain = Domain,
        ProtectedPassword = ProtectedPassword,
        MacAddress = MacAddress,
    };
}

/// <summary>DPAPI (CurrentUser scope) wrapper. No key management on our side.</summary>
public static class SecretVault
{
    public static string Protect(SecureString pwd)
    {
        var ptr = Marshal.SecureStringToBSTR(pwd);
        try
        {
            var text = Marshal.PtrToStringBSTR(ptr) ?? "";
            var bytes = Encoding.UTF8.GetBytes(text);
            var enc = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(enc);
        }
        finally { Marshal.ZeroFreeBSTR(ptr); }
    }

    public static SecureString Unprotect(string base64)
    {
        var enc = Convert.FromBase64String(base64);
        var plain = ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser);
        var text = Encoding.UTF8.GetString(plain);
        Array.Clear(plain);
        var s = new SecureString();
        foreach (var c in text) s.AppendChar(c);
        text = string.Empty;
        return s;
    }

    public static string ConsumeToPlain(SecureString pwd)
    {
        var ptr = Marshal.SecureStringToBSTR(pwd);
        try { return Marshal.PtrToStringBSTR(ptr) ?? ""; }
        finally { Marshal.ZeroFreeBSTR(ptr); }
    }

    /// <summary>Same protection, string in/out. Used for the AI API key.</summary>
    public static string ProtectText(string plain)
    {
        var bytes = Encoding.UTF8.GetBytes(plain);
        var enc = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(enc);
    }

    public static string UnprotectText(string? base64)
    {
        if (string.IsNullOrEmpty(base64)) return "";
        try
        {
            var enc = Convert.FromBase64String(base64);
            var plain = ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch { return ""; } // A key saved by another Windows user cannot be read
    }
}
