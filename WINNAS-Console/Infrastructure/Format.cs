using System.Globalization;

namespace Winnas.Console.Infrastructure;

public static class Format
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB", "PB" };

    public static string Bytes(long? bytes)
    {
        if (bytes is null || bytes < 0) return "—";
        double v = bytes.Value;
        int u = 0;
        while (v >= 1024 && u < Units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{v:0} B" : $"{v:0.##} {Units[u]}";
    }

    public static string Uptime(TimeSpan? ts)
    {
        if (ts is null) return "—";
        var t = ts.Value;
        if (t.TotalMinutes < 1) return "刚刚启动";
        if (t.TotalHours < 1) return $"{t.Minutes} 分钟";
        if (t.TotalDays < 1) return $"{t.Hours} 小时 {t.Minutes} 分";
        return $"{t.Days} 天 {t.Hours} 小时 {t.Minutes} 分";
    }

    public static string Date(DateTime? dt)
    {
        if (dt is null) return "—";
        return dt.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    public static string DateLong(DateTime? dt)
    {
        if (dt is null) return "—";
        return dt.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    public static string Hours(long? hours)
    {
        if (hours is null) return "—";
        if (hours.Value <= 0) return "0 小时";
        return $"{hours.Value:N0} 小时";
    }

    public static string Temp(int? celsius)
    {
        if (celsius is null) return "—";
        return $"{celsius} °C";
    }

    public static string Count(long? n)
    {
        if (n is null) return "—";
        return n.Value.ToString("N0", CultureInfo.InvariantCulture);
    }

    public static string Percent(double used, double total)
    {
        if (total <= 0) return "0%";
        return $"{(used / total) * 100:0.#}%";
    }

    /// <summary>Bytes per second as a rate, the unit live charts speak in.</summary>
    public static string Rate(long bytesPerSecond)
    {
        if (bytesPerSecond < 0) return "—";
        if (bytesPerSecond < 1024) return $"{bytesPerSecond} B/s";
        return $"{Bytes(bytesPerSecond)}/s";
    }
}
