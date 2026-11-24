using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Winnas.Console.Models;

namespace Winnas.Console.Infrastructure;

public sealed class AppSettings
{
    [JsonPropertyName("connections")]
    public List<ConnectionProfile> Connections { get; set; } = new();

    [JsonPropertyName("lastConnectionId")]
    public string? LastConnectionId { get; set; }

    [JsonPropertyName("backupTask")]
    public string BackupTask { get; set; } = "NAS Local Mirror";

    [JsonPropertyName("backupSource")]
    public string BackupSource { get; set; } = "D:\\NAS";

    [JsonPropertyName("backupLogDir")]
    public string BackupLogDir { get; set; } = "C:\\Users\\Shawn\\NAS-Backup\\logs";

    [JsonPropertyName("refreshSeconds")]
    public int RefreshSeconds { get; set; } = 10;

    /// <summary>Skin key from Themes/Palette.*.xaml; empty means the default.</summary>
    [JsonPropertyName("theme")]
    public string Theme { get; set; } = ThemeManager.DefaultKey;

    /// <summary>Title of the page to reopen on startup; empty means the first one.</summary>
    [JsonPropertyName("lastPageTitle")]
    public string LastPageTitle { get; set; } = "";

    [JsonPropertyName("watchedServices")]
    public List<string> WatchedServices { get; set; } = new()
    {
        "LanmanServer", "LanmanWorkstation", "Schedule", "WSearch", "WinDefend", "Spooler",
    };

    [JsonPropertyName("snapshotPolicies")]
    public List<SnapshotPolicy> SnapshotPolicies { get; set; } = new();

    /// <summary>Backup definitions authored here and executed on the target.</summary>
    [JsonPropertyName("backupPlans")]
    public List<BackupPlan> BackupPlans { get; set; } = new();

    /// <summary>Where robocopy logs are written on the managed machine.</summary>
    [JsonPropertyName("backupPlanLogDir")]
    public string BackupPlanLogDir { get; set; } = @"C:\ProgramData\WINNAS-Console\backup-logs";

    /// <summary>Show the "what this page does" strip above every page.</summary>
    [JsonPropertyName("showPageHelp")]
    public bool ShowPageHelp { get; set; } = true;

    /// <summary>Day files older than this are deleted by the log viewer.</summary>
    [JsonPropertyName("logKeepDays")]
    public int LogKeepDays { get; set; } = 7;

    /// <summary>Set once the first-run hint has been shown.</summary>
    [JsonPropertyName("firstRunDone")]
    public bool FirstRunDone { get; set; }

    // ------------------------------------------------------------- AI

    [JsonPropertyName("aiBaseUrl")]
    public string AiBaseUrl { get; set; } = "https://api.deepseek.com";

    [JsonPropertyName("aiModel")]
    public string AiModel { get; set; } = "deepseek-chat";

    /// <summary>DPAPI-protected under the current user; never stored in plain text.</summary>
    [JsonPropertyName("aiApiKey")]
    public string AiApiKey { get; set; } = "";

    [JsonPropertyName("aiTemperature")]
    public double AiTemperature { get; set; } = 0.3;

    [JsonPropertyName("aiSystemPrompt")]
    public string AiSystemPrompt { get; set; } = DefaultAiSystemPrompt;

    [JsonPropertyName("aiAttachContext")]
    public bool AiAttachContext { get; set; } = true;

    public const string DefaultAiSystemPrompt =
        "你是 WINNAS（一台 Windows NAS 的桌面管理控制台）内置的运维助手。" +
        "回答要具体、可执行，优先给出能在 Windows 上直接运行的命令或操作路径。" +
        "不确定就说不确定，不要编造参数。回答用简体中文，简洁克制，不写客套话。";
}

/// <summary>
/// Persists connections and settings under %LOCALAPPDATA%\WINNAS-Console.
/// Passwords are stored DPAPI-protected under the current user account.
/// </summary>
public static class AppState
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static string Dir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WINNAS-Console");

    public static string FilePath { get; } = Path.Combine(Dir, "config.json");

    public static AppSettings Settings { get; private set; } = Load();

    public static AppSettings Load()
    {
        try
        {
            if (System.IO.File.Exists(FilePath))
            {
                var text = System.IO.File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(text, Json);
                if (loaded is not null)
                {
                    if (loaded.Connections.Count == 0)
                        loaded.Connections.Add(new ConnectionProfile());
                    return loaded;
                }
            }
        }
        catch { /* corrupt config must not block startup */ }

        return new AppSettings { Connections = { new ConnectionProfile() } };
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var tmp = FilePath + ".tmp";
            System.IO.File.WriteAllText(tmp, JsonSerializer.Serialize(Settings, Json));
            System.IO.File.Move(tmp, FilePath, overwrite: true);
        }
        catch { /* never let a config write crash a monitoring tool */ }
    }

    public static ConnectionProfile? Find(string? id) =>
        Settings.Connections.FirstOrDefault(c => c.Id == id);

    public static ConnectionProfile Current() =>
        Find(Settings.LastConnectionId) ?? Settings.Connections.FirstOrDefault() ?? new ConnectionProfile();

    public static void Upsert(ConnectionProfile profile)
    {
        var idx = Settings.Connections.FindIndex(c => c.Id == profile.Id);
        if (idx >= 0) Settings.Connections[idx] = profile;
        else Settings.Connections.Add(profile);
        Save();
    }

    public static BackupPlan? FindPlan(string? id) =>
        Settings.BackupPlans.FirstOrDefault(p => p.Id == id);

    public static void Remove(string id)
    {
        Settings.Connections.RemoveAll(c => c.Id == id);
        if (Settings.LastConnectionId == id) Settings.LastConnectionId = null;
        Save();
    }
}
