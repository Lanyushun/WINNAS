using System.Globalization;
using System.IO;
using System.Text;
using Winnas.Console.Models;

namespace Winnas.Console.Infrastructure;

public enum LogLevel
{
    Info,
    Ok,
    Warn,
    Error,
}

/// <summary>
/// One line of the activity journal. Kept deliberately small: it is written to
/// disk on every action, so it must stay cheap to format.
/// </summary>
public sealed class LogEntry
{
    public DateTime Time { get; init; } = DateTime.Now;
    public LogLevel Level { get; init; }
    public string Category { get; init; } = "";
    public string Message { get; init; } = "";
    public string? Detail { get; init; }

    public string TimeText => Time.ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    public string FullTimeText => Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public string LevelText => Level switch
    {
        LogLevel.Ok => "成功",
        LogLevel.Warn => "警告",
        LogLevel.Error => "错误",
        _ => "信息",
    };

    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);

    public string Line => HasDetail ? $"{Message} — {Detail}" : Message;

    public string ToFileLine() =>
        string.Join('\t',
            Time.ToString("o", CultureInfo.InvariantCulture),
            ((int)Level).ToString(CultureInfo.InvariantCulture),
            Sanitize(Category),
            Sanitize(Message),
            Sanitize(Detail ?? ""));

    private static string Sanitize(string s) =>
        s.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");

    public static LogEntry? FromFileLine(string line)
    {
        var parts = line.TrimStart('\uFEFF').Split('\t');
        if (parts.Length < 4) return null;

        if (!DateTime.TryParse(parts[0], CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var time))
            return null;

        _ = int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var level);

        return new LogEntry
        {
            Time = time,
            Level = level is >= 0 and <= 3 ? (LogLevel)level : LogLevel.Info,
            Category = parts[2],
            Message = parts[3],
            Detail = parts.Length > 4 && parts[4].Length > 0 ? parts[4] : null,
        };
    }
}

/// <summary>
/// The journal every mutating action and every failure is written to.
///
/// Two things matter here: it has to survive a crash (so each line is flushed
/// immediately rather than buffered), and it must never throw — a monitoring
/// tool that dies because its own log file was locked is worse than useless.
/// </summary>
public static class ActivityLog
{
    private const int MemoryLimit = 400;

    private static readonly object Gate = new();

    /// <summary>Separate from Gate: a slow disk must not block readers of the buffer.</summary>
    private static readonly object WriteGate = new();

    private static readonly List<LogEntry> Recent = new();
    private static StreamWriter? _writer;
    private static string _currentDay = "";

    public static string Dir { get; } = Path.Combine(AppState.Dir, "logs");

    /// <summary>Raised on the UI thread's behalf; subscribers must marshal themselves.</summary>
    public static event Action<LogEntry>? EntryAdded;

    public static IReadOnlyList<LogEntry> Snapshot()
    {
        lock (Gate) return Recent.ToList();
    }

    public static void Write(LogLevel level, string category, string message, string? detail = null)
    {
        var entry = new LogEntry
        {
            Level = level,
            Category = category,
            Message = message,
            Detail = detail,
        };

        lock (Gate)
        {
            Recent.Add(entry);
            while (Recent.Count > MemoryLimit) Recent.RemoveAt(0);
        }

        // Pages and background refreshes log from different threads; without
        // this lock the append handle itself races and lines interleave.
        lock (WriteGate)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var day = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (_writer is null || day != _currentDay)
                {
                    _writer?.Dispose();
                    _currentDay = day;
                    _writer = new StreamWriter(Path.Combine(Dir, $"activity-{day}.log"), true, Encoding.UTF8)
                    {
                        AutoFlush = true,
                    };
                }

                _writer.WriteLine(entry.ToFileLine());
            }
            catch { /* logging must never break the app */ }
        }

        EntryAdded?.Invoke(entry);
    }

    public static void Info(string category, string message, string? detail = null) =>
        Write(LogLevel.Info, category, message, detail);

    public static void Ok(string category, string message, string? detail = null) =>
        Write(LogLevel.Ok, category, message, detail);

    public static void Warn(string category, string message, string? detail = null) =>
        Write(LogLevel.Warn, category, message, detail);

    public static void Error(string category, string message, string? detail = null) =>
        Write(LogLevel.Error, category, message, detail);

    /// <summary>Convenience for "the operation returned an OpResult" sites.</summary>
    public static void Result(string category, OpResult result, string what)
    {
        if (result.Ok) Ok(category, result.Message, what);
        else Error(category, result.Message, what);
    }

    /// <summary>Loads recent history from disk, newest first, capped.</summary>
    public static List<LogEntry> LoadHistory(int days = 3, int limit = 500)
    {
        var list = new List<LogEntry>();
        try
        {
            if (!Directory.Exists(Dir)) return list;

            var files = new DirectoryInfo(Dir)
                .EnumerateFiles("activity-*.log")
                .OrderByDescending(f => f.Name)
                .Take(days);

            foreach (var f in files)
            {
                // ReadWrite sharing is mandatory: this very process may still
                // hold the append handle, and the default FileShare.Read from a
                // reader conflicts with that writer's Write access.
                using var stream = new FileStream(f.FullName, FileMode.Open,
                    FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, new UTF8Encoding(false));

                while (reader.ReadLine() is { } raw)
                {
                    var e = LogEntry.FromFileLine(raw);
                    if (e is not null) list.Add(e);
                }
            }
        }
        catch { /* a locked or missing history is not fatal */ }

        list.Sort((a, b) => b.Time.CompareTo(a.Time));
        return list.Take(limit).ToList();
    }

    /// <summary>Drops day files older than the retention window.</summary>
    public static int Prune(int keepDays)
    {
        var removed = 0;
        try
        {
            if (!Directory.Exists(Dir)) return 0;

            var cutoff = DateTime.Now.Date.AddDays(-Math.Max(1, keepDays));
            foreach (var f in new DirectoryInfo(Dir).EnumerateFiles("activity-*.log"))
            {
                if (f.LastWriteTime.Date >= cutoff) continue;
                try { f.Delete(); removed++; }
                catch { /* in-use file: leave it */ }
            }
        }
        catch { /* ignore */ }

        return removed;
    }

    public static void ClearMemory()
    {
        lock (Gate) Recent.Clear();
    }

    /// <summary>Whole journal as text, for the export button.</summary>
    public static string ToText(IEnumerable<LogEntry> entries)
    {
        var sb = new StringBuilder();
        foreach (var e in entries)
        {
            sb.Append('[').Append(e.FullTimeText).Append("] ")
              .Append(e.LevelText).Append(" | ")
              .Append(e.Category).Append(" | ")
              .Append(e.Message);
            if (e.HasDetail) sb.Append(" — ").Append(e.Detail);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public static void Shutdown()
    {
        lock (WriteGate)
        {
            try { _writer?.Dispose(); }
            catch { /* ignore */ }
            _writer = null;
        }
    }

    /// <summary>
    /// Drops the append handle so the file can be deleted. The next Write
    /// reopens it; without this, clearing the log would silently fail because
    /// our own handle is still open.
    /// </summary>
    public static void ReleaseFile()
    {
        lock (WriteGate)
        {
            try { _writer?.Dispose(); }
            catch { /* ignore */ }
            _writer = null;
        }
    }
}
