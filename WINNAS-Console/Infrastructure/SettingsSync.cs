using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Winnas.Console.Models;
using Winnas.Console.Services;

namespace Winnas.Console.Infrastructure;

/// <summary>
/// A portable copy of this console's configuration, meant to travel to the
/// managed machine (or to a file) and come back.
///
/// Two fields can NEVER travel: connection passwords and the AI API key are
/// DPAPI-protected under the *current Windows user*. On another machine (or
/// even another account on this one) the ciphertext is undecipherable garbage.
/// Shipping it would look like a successful sync and then silently break every
/// saved connection. They are stripped on capture and re-attached from the
/// local config on restore, keyed by connection Id.
///
/// Window geometry is stripped too: a headless NAS has no window, and there is
/// no reason two machines should argue about where a window belongs.
/// </summary>
public sealed class SettingsSnapshot
{
    [JsonPropertyName("schema")]
    public int Schema { get; set; } = 1;

    [JsonPropertyName("capturedAt")]
    public string CapturedAt { get; set; } = "";

    [JsonPropertyName("sourceMachine")]
    public string SourceMachine { get; set; } = "";

    [JsonPropertyName("settings")]
    public JsonObject Settings { get; set; } = new();
}

/// <summary>What is sitting on the target right now, without applying any of it.</summary>
public sealed record TargetSettingsInfo(
    bool Exists,
    string Message,
    DateTime? WrittenAt,
    string Fingerprint,
    string SourceMachine,
    string CapturedAt,
    int Connections,
    int BackupPlans,
    int SnapshotPolicies)
{
    public static TargetSettingsInfo Missing(string message) =>
        new(false, message, null, "", "", "", 0, 0, 0);
}

public static class SettingsSync
{
    public const int Schema = 1;
    public const string SettingsFileName = "console-settings.json";

    public static string TargetPath => AgentDeployer.AgentDir + @"\" + SettingsFileName;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions Flat = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Fields that exist only on this desktop and must not cross the wire.</summary>
    private static readonly string[] DesktopOnly =
    {
        "windowLeft", "windowTop", "windowWidth", "windowHeight", "windowMaximized",
        // Which connection this desktop last used is local navigation state.
        // Pushing it would make the NAS copy claim a "current" connection that
        // belongs to a different machine.
        "lastConnectionId",
        // lastPageTitle changes every time a sidebar page is clicked, which
        // would make the fingerprints disagree forever. firstRunDone governs a
        // one-time toast on THIS desktop only.
        "lastPageTitle",
        "firstRunDone",
    };

    // ------------------------------------------------------------- capture

    /// <summary>
    /// Snapshot the live settings minus every secret and every desktop-only
    /// field. Capturing through JSON rather than a hand-written DTO means a new
    /// AppSettings property is included automatically — there is no list to
    /// forget to update.
    /// </summary>
    public static SettingsSnapshot Capture()
    {
        var body = (JsonObject)JsonSerializer.SerializeToNode(AppState.Settings, Json)!.DeepClone();

        // Secrets: DPAPI(CurrentUser), useless and misleading on another host.
        body.Remove("aiApiKey");
        if (body["connections"] is JsonArray connections)
            foreach (var node in connections)
                node?.AsObject().Remove("pwd");

        foreach (var key in DesktopOnly) body.Remove(key);

        return new SettingsSnapshot
        {
            Schema = Schema,
            CapturedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            SourceMachine = Environment.MachineName,
            Settings = body,
        };
    }

    public static string Serialize(SettingsSnapshot snapshot) =>
        JsonSerializer.Serialize(snapshot, Json);

    public static SettingsSnapshot? Parse(string text)
    {
        try
        {
            var s = JsonSerializer.Deserialize<SettingsSnapshot>(text, Json);
            return s is { Schema: <= Schema } && s.Settings is not null ? s : null;
        }
        catch { return null; }
    }

    /// <summary>Stable hash of the payload only — the timestamp is metadata, not content.</summary>
    public static string Fingerprint(SettingsSnapshot snapshot)
    {
        var canonical = snapshot.Settings.ToJsonString(Flat);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..12];
    }

    // -------------------------------------------------------------- restore

    /// <summary>
    /// Merge a snapshot back into the live settings. Local secrets survive by
    /// Id; connections that exist only in the snapshot arrive with no password,
    /// which is honest — the user will be asked to type it once.
    /// </summary>
    public static OpResult Apply(SettingsSnapshot snapshot)
    {
        try
        {
            var body = (JsonObject)snapshot.Settings.DeepClone();

            var local = AppState.Settings;
            var localKey = local.AiApiKey;
            var localPwds = local.Connections
                .Where(c => c.ProtectedPassword is not null)
                .ToDictionary(c => c.Id, c => c.ProtectedPassword, StringComparer.Ordinal);

            body["aiApiKey"] = localKey;
            body["lastConnectionId"] = local.LastConnectionId;
            body["windowLeft"] = local.WindowLeft;
            body["windowTop"] = local.WindowTop;
            body["windowWidth"] = local.WindowWidth;
            body["windowHeight"] = local.WindowHeight;
            body["windowMaximized"] = local.WindowMaximized;

            if (body["connections"] is JsonArray connections)
            {
                foreach (var node in connections)
                {
                    if (node is not JsonObject c) continue;
                    var id = c["id"]?.GetValue<string>() ?? "";
                    if (id.Length > 0 && localPwds.TryGetValue(id, out var pwd) && pwd is not null)
                        c["pwd"] = pwd;
                    else
                        c.Remove("pwd");
                }
            }

            var restored = JsonSerializer.Deserialize<AppSettings>(body.ToJsonString(Json), Json)
                           ?? throw new InvalidOperationException("快照内容不是有效的配置。");

            // Keep the AppSettings instance itself: pages hold references to its
            // collections, and replacing the object would leave them stale.
            MergeInto(local, restored);
            AppState.Save();
            ThemeManager.Apply(local.Theme);

            return OpResult.Good(
                $"已恢复来自 {snapshot.SourceMachine} 的设置（{snapshot.CapturedAt}）：" +
                $"{restored.Connections.Count} 条连接、{restored.BackupPlans.Count} 个备份方案、" +
                $"{restored.SnapshotPolicies.Count} 条快照策略。本机保存的密码与 API Key 未改动。");
        }
        catch (Exception ex)
        {
            return OpResult.Fail("恢复设置失败：" + OpResult.From(ex).Message);
        }
    }

    private static void MergeInto(AppSettings into, AppSettings from)
    {
        into.Connections.Clear();
        into.Connections.AddRange(from.Connections);
        into.WatchedServices.Clear();
        into.WatchedServices.AddRange(from.WatchedServices);
        into.SnapshotPolicies.Clear();
        into.SnapshotPolicies.AddRange(from.SnapshotPolicies);
        into.BackupPlans.Clear();
        into.BackupPlans.AddRange(from.BackupPlans);

        into.BackupTask = from.BackupTask;
        into.BackupSource = from.BackupSource;
        into.BackupLogDir = from.BackupLogDir;
        into.RefreshSeconds = from.RefreshSeconds;
        into.Theme = from.Theme;
        into.BackupPlanLogDir = from.BackupPlanLogDir;
        into.ShowPageHelp = from.ShowPageHelp;
        into.LogKeepDays = from.LogKeepDays;
        into.AiBaseUrl = from.AiBaseUrl;
        into.AiModel = from.AiModel;
        into.AiTemperature = from.AiTemperature;
        into.AiSystemPrompt = from.AiSystemPrompt;
        into.AiAttachContext = from.AiAttachContext;
        into.SyncAutoPush = from.SyncAutoPush;
        into.TimeAutoSync = from.TimeAutoSync;
        into.TimeToleranceSeconds = from.TimeToleranceSeconds;
        // Intentionally NOT merged, because they never left this machine (see
        // DesktopOnly) or are secrets bound to the current Windows account:
        //   AiApiKey, window geometry, LastConnectionId, LastPageTitle, FirstRunDone
        // The snapshot's own copies of those already hold the local values, so
        // leaving the live ones untouched is a no-op, not data loss.
    }

    // ---------------------------------------------------------- target I/O

    public static Task<OpResult> PushAsync(WinnasSession session) => Task.Run(() =>
    {
        try
        {
            if (!session.IsRemote)
                return OpResult.Fail("当前连接是本机，没有可同步的远端。" );

            using (session.Impersonate())
            {
                var path = WinnasService.PathFor(session, TargetPath);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, Serialize(Capture()), new UTF8Encoding(false));
            }
            return OpResult.Good($"设置已写入被控端 {TargetPath}（密码与 API Key 未包含）");
        }
        catch (Exception ex)
        {
            return OpResult.Fail("下发设置失败：" + OpResult.From(ex).Message +
                "。需要能访问被控端的管理共享 C$。");
        }
    });

    public static Task<(OpResult Result, SettingsSnapshot? Snapshot)> PullAsync(WinnasSession session) =>
        Task.Run(() =>
        {
            try
            {
                if (!session.IsRemote)
                    return (OpResult.Fail("当前连接是本机，没有可读取的远端副本。"), (SettingsSnapshot?)null);

                string text;
                using (session.Impersonate())
                {
                    var path = WinnasService.PathFor(session, TargetPath);
                    if (!File.Exists(path))
                        return (OpResult.Fail($"被控端还没有设置副本（{TargetPath}）。先点一次「下发到 NAS」。"), (SettingsSnapshot?)null);
                    text = File.ReadAllText(path);
                }

                var snapshot = Parse(text);
                return snapshot is null
                    ? (OpResult.Fail("被控端的设置副本无法解析，可能由旧版本写入或文件已损坏。"), (SettingsSnapshot?)null)
                    : (OpResult.Good("已读取被控端设置副本"), snapshot);
            }
            catch (Exception ex)
            {
                return (OpResult.Fail("读取被控端设置失败：" + OpResult.From(ex).Message), (SettingsSnapshot?)null);
            }
        });

    /// <summary>Read-only look at what is on the target, for the page's status card.</summary>
    public static Task<TargetSettingsInfo> InspectAsync(WinnasSession session) => Task.Run(() =>
    {
        try
        {
            if (!session.IsRemote)
                return TargetSettingsInfo.Missing("当前连接是本机，设置同步只对远程被控端有意义。");

            string text;
            DateTime written;
            using (session.Impersonate())
            {
                var path = WinnasService.PathFor(session, TargetPath);
                if (!File.Exists(path))
                    return TargetSettingsInfo.Missing("被控端还没有设置副本，点「下发到 NAS」建立第一份。");
                written = File.GetLastWriteTime(path);
                text = File.ReadAllText(path);
            }

            var snapshot = Parse(text);
            if (snapshot is null)
                return TargetSettingsInfo.Missing("被控端的设置副本无法解析。");

            int connections = snapshot.Settings["connections"] is JsonArray c ? c.Count : 0;
            int plans = snapshot.Settings["backupPlans"] is JsonArray p ? p.Count : 0;
            int policies = snapshot.Settings["snapshotPolicies"] is JsonArray sp ? sp.Count : 0;

            return new TargetSettingsInfo(true, "已读取被控端副本", written,
                Fingerprint(snapshot), snapshot.SourceMachine, snapshot.CapturedAt,
                connections, plans, policies);
        }
        catch (Exception ex)
        {
            return TargetSettingsInfo.Missing("读取被控端设置失败：" + OpResult.From(ex).Message);
        }
    });

    /// <summary>Compare the local payload with what is on the target.</summary>
    public static bool MatchesLocal(SettingsSnapshot snapshot) =>
        Fingerprint(snapshot) == Fingerprint(Capture());

    // --------------------------------------------------------------- files

    public static OpResult ExportTo(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(full, Serialize(Capture()), new UTF8Encoding(false));
            return OpResult.Good("已导出设置副本（不含密码与 API Key）：" + full);
        }
        catch (Exception ex)
        {
            return OpResult.Fail("导出失败：" + OpResult.From(ex).Message);
        }
    }

    public static (OpResult Result, SettingsSnapshot? Snapshot) ImportFrom(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (!File.Exists(full)) return (OpResult.Fail("文件不存在：" + full), null);
            var snapshot = Parse(File.ReadAllText(full));
            return snapshot is null
                ? (OpResult.Fail("文件不是有效的设置副本。"), null)
                : (OpResult.Good("已读取设置副本"), snapshot);
        }
        catch (Exception ex)
        {
            return (OpResult.Fail("读取文件失败：" + OpResult.From(ex).Message), null);
        }
    }

    /// <summary>Default export location, next to config.json.</summary>
    public static string DefaultExportPath => Path.Combine(AppState.Dir, "console-settings.json");

    // ---------------------------------------------------- backup plans view

    /// <summary>
    /// How many plan definitions the target already has in its sidecar file.
    /// The agent reads that file when a scheduled task fires; the console's own
    /// list lives in config.json. Showing both is the only way to see whether
    /// they still agree.
    /// </summary>
    public static Task<(int Count, List<string> Names, string Message)> InspectPlansAsync(WinnasSession session) =>
        Task.Run(() =>
        {
            try
            {
                if (!session.IsRemote)
                    return (0, new List<string>(), "当前连接是本机，无需下发方案。");

                string text;
                using (session.Impersonate())
                {
                    var path = WinnasService.PathFor(session, AgentDeployer.PlanFile);
                    if (!File.Exists(path))
                        return (0, new List<string>(), "被控端还没有方案副本，点「下发方案到 NAS」建立第一份。");
                    text = File.ReadAllText(path);
                }

                var plans = JsonSerializer.Deserialize<List<BackupPlan>>(text, Json) ?? new List<BackupPlan>();
                return (plans.Count, plans.Select(p => p.Name).ToList(),
                    $"被控端已有 {plans.Count} 个方案定义（backup-plans.json）");
            }
            catch (Exception ex)
            {
                return (0, new List<string>(), "读取被控端方案失败：" + OpResult.From(ex).Message);
            }
        });
}
