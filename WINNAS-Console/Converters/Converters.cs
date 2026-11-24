using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Winnas.Console.Infrastructure;

namespace Winnas.Console.Converters;

public class BytesConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            null => "—",
            long l => Format.Bytes(l),
            int i => Format.Bytes(i),
            _ => Format.Bytes(0),
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class UptimeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is TimeSpan ts ? Format.Uptime(ts) : "—";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class DateConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Format.Date(value as DateTime?);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class HoursConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null) return "—";
        if (value is long l) return Format.Hours(l);
        if (value is int i) return Format.Hours(i);
        return "—";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class TemperatureConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null) return "—";
        if (value is int c) return Format.Temp(c);
        if (value is long l) return Format.Temp((int)l);
        return "—";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Usage ratio (0..1) to a traffic-light colour.</summary>
public class UsageBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double ratio = 0;
        if (value is double d) ratio = d;
        else if (value is decimal m) ratio = (double)m;
        else if (value is float f) ratio = f;
        else if (value is int i) ratio = i / 100.0;
        else return new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A));

        return ratio >= 0.9
            ? new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26))
            : ratio >= 0.7
                ? new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06))
                : new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Storage health code to a coloured dot.</summary>
public class HealthBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int code)
        {
            return code == 0 ? new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A))
                 : code == 1 ? new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06))
                 : code == 2 ? new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26))
                 : new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));
        }
        return new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool flag = value is bool b && b;
        bool invert = parameter?.ToString() == "inverse";
        if (invert) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility v && v == Visibility.Visible;
}

/// <summary>Inverts a boolean; used to disable buttons while work is in flight.</summary>
public class InvertBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && !b;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && !b;
}

public class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = value as string ?? "";
        var visible = text.Length > 0;
        if (parameter?.ToString() == "inverse") visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Share ACLs as one line: "Everyone 完全控制 · 允许".</summary>
public class ShareAccessConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not IEnumerable<Models.ShareAce> aces) return "—";
        var text = string.Join(" ｜ ", aces.Select(a => $"{a.AccountName} {a.RightText} · {a.TypeText}"));
        return text.Length == 0 ? "（无）" : text;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Partitions of a disk as one compact line: "C: 118 GB · D: 465 GB".</summary>
public class PartitionsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not IEnumerable<Models.PartitionBrief> parts) return "—";
        var text = string.Join(" · ", parts.Select(p => $"{p.DriveLetter} {Format.Bytes(p.Size)}"));
        return text.Length == 0 ? "—" : text;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Non-zero counts become visible; zero collapses. Used for badges.</summary>
public class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var n = value switch
        {
            int i => i,
            long l => (int)l,
            _ => 0,
        };

        var visible = n > 0;
        if (parameter?.ToString() == "inverse") visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Non-null becomes visible; null collapses. For "no data yet" panels.</summary>
public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var present = value is not null;
        if (value is string s) present = s.Length > 0;
        if (parameter?.ToString() == "inverse") present = !present;
        return present ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Journal level to a colour dot.</summary>
public class LogLevelBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var level = value is Infrastructure.LogLevel l ? l : Infrastructure.LogLevel.Info;
        return level switch
        {
            Infrastructure.LogLevel.Error => new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26)),
            Infrastructure.LogLevel.Warn => new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06)),
            Infrastructure.LogLevel.Ok => new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A)),
            _ => new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)),
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Health finding severity to a colour.</summary>
public class SeverityBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var sev = value is Services.Severity s ? s : Services.Severity.Info;
        return sev switch
        {
            Services.Severity.Critical => new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26)),
            Services.Severity.Warn => new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06)),
            _ => new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)),
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Health score 0..100 to a colour.</summary>
public class ScoreBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var score = value is int i ? i : 100;
        return score >= 90
            ? new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A))
            : score >= 70
                ? new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06))
                : new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Converts a usage ratio (0..1) into a percentage label.</summary>
public class PercentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double d) return $"{d * 100:0.#}%";
        if (value is decimal m) return $"{m * 100:0.#}%";
        if (value is int i) return $"{i}%";
        return "—";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
