using System.Management;

namespace Winnas.Console.Infrastructure;

/// <summary>
/// Safe accessors over CimInstance properties. CIM returns null for every
/// property the target cannot supply (e.g. NVMe disks report no power-on
/// hours), so every read must tolerate absence instead of throwing.
///
/// IMPORTANT: MI already converts CIM DATETIME values to DateTimeKind.Local.
/// Verified against Environment.TickCount64 on this machine (37.10 h both
/// ways). Never re-interpret these as UTC — it would shift everything by 8 h.
/// </summary>
public static class Cim
{
    public const string StorageNs = @"root\Microsoft\Windows\Storage";
    public const string SmbNs = @"root\Microsoft\Windows\SMB";
    public const string CimV2 = @"root\CIMV2";
    public const string StdCimV2 = @"root\StandardCimv2";
    public const string TaskNs = @"root\Microsoft\Windows\TaskScheduler";

    public static string Str(ManagementBaseObject i, string name, string fallback = "")
    {
        var v = i.Properties[name]?.Value;
        if (v is null) return fallback;
        if (v is string s) return s.Length > 0 ? s : fallback;
        return v.ToString() ?? fallback;
    }

    public static int Int(ManagementBaseObject i, string name, int fallback = 0)
    {
        var v = i.Properties[name]?.Value;
        if (v is null) return fallback;
        try { return Convert.ToInt32(v); }
        catch { return fallback; }
    }

    public static long Long(ManagementBaseObject i, string name, long fallback = 0)
    {
        var v = i.Properties[name]?.Value;
        if (v is null) return fallback;
        try { return Convert.ToInt64(v); }
        catch { return fallback; }
    }

    public static bool Bool(ManagementBaseObject i, string name, bool fallback = false)
    {
        var v = i.Properties[name]?.Value;
        if (v is null) return fallback;
        if (v is bool b) return b;
        try { return Convert.ToInt32(v) != 0; }
        catch { return fallback; }
    }

    public static DateTime? Date(ManagementBaseObject i, string name)
    {
        var v = i.Properties[name]?.Value;
        if (v is DateTime dt)
        {
            // MI tags these Local; if it ever comes as UTC we convert once.
            return dt.Kind == DateTimeKind.Utc ? dt.ToLocalTime() : dt;
        }
        if (v is string s && !string.IsNullOrWhiteSpace(s)) return ParseCimDateTime(s);
        return null;
    }

    /// <summary>Parse a raw CIM DMTF datetime string, e.g. 20261021124352.000000+480.</summary>
    public static DateTime? ParseCimDateTime(string s)
    {
        // DMTF: yyyymmddHHMMSS.mmmmmm+ooo
        if (s.Length < 14 || !long.TryParse(s[..14], out _)) return null;
        try
        {
            int year = int.Parse(s[..4], System.Globalization.CultureInfo.InvariantCulture);
            int month = int.Parse(s.Substring(4, 2), System.Globalization.CultureInfo.InvariantCulture);
            int day = int.Parse(s.Substring(6, 2), System.Globalization.CultureInfo.InvariantCulture);
            int hour = int.Parse(s.Substring(8, 2), System.Globalization.CultureInfo.InvariantCulture);
            int min = int.Parse(s.Substring(10, 2), System.Globalization.CultureInfo.InvariantCulture);
            int sec = int.Parse(s.Substring(12, 2), System.Globalization.CultureInfo.InvariantCulture);
            var local = new DateTime(year, month, day, hour, min, sec, DateTimeKind.Local);
            // offset part, when present, means the literal is not local time
            if (s.Length >= 22)
            {
                var offsetText = s[^4..];
                if (int.TryParse(offsetText, System.Globalization.CultureInfo.InvariantCulture, out var minutes))
                    return local.AddMinutes(-minutes).ToLocalTime();
            }
            return local;
        }
        catch { return null; }
    }

    /// <summary>MSFT_Volume.DriveLetter is a Char; an empty one is '\0', not null.</summary>
    public static string DriveLetter(ManagementBaseObject i, string name)
    {
        var v = i.Properties[name]?.Value;
        if (v is null) return "";
        if (v is char c) return c == '\0' ? "" : c.ToString();
        if (v is string s) return s.Trim();
        return v.ToString() ?? "";
    }
}
