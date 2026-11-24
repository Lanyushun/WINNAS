using System.IO;
using System.Text.Json;
using Winnas.Console.Models;

namespace Winnas.Console.Infrastructure;

/// <summary>
/// History of every dry run and real run, newest first, capped so the file
/// cannot grow without bound on a machine that backs up hourly.
/// Kept out of config.json: it is data, not settings, and it churns.
/// </summary>
public static class BackupRunStore
{
    private const int MaxRuns = 300;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly object Gate = new();

    public static string FilePath { get; } = Path.Combine(AppState.Dir, "backup-runs.json");

    public static List<BackupRun> Load()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(FilePath)) return new List<BackupRun>();
                var text = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<List<BackupRun>>(text, Json) ?? new List<BackupRun>();
            }
            catch { return new List<BackupRun>(); }
        }
    }

    public static void Add(BackupRun run)
    {
        lock (Gate)
        {
            try
            {
                var all = Load();
                all.Insert(0, run);
                if (all.Count > MaxRuns) all = all.Take(MaxRuns).ToList();

                Directory.CreateDirectory(AppState.Dir);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(all, Json));
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch { /* history is never worth crashing a backup over */ }
        }
    }

    public static List<BackupRun> For(string? planId)
    {
        var all = Load();
        return string.IsNullOrWhiteSpace(planId) ? all : all.Where(r => r.PlanId == planId).ToList();
    }

    public static void Clear(string? planId)
    {
        lock (Gate)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(planId))
                {
                    File.Delete(FilePath);
                    return;
                }
                var rest = Load().Where(r => r.PlanId != planId).ToList();
                File.WriteAllText(FilePath, JsonSerializer.Serialize(rest, Json));
            }
            catch { /* ignore */ }
        }
    }
}
