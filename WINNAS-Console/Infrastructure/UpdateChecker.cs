using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Winnas.Console.Services;

namespace Winnas.Console.Infrastructure;

public enum UpdateKind
{
    /// <summary>Local version is the same as, or newer than, the newest release.</summary>
    UpToDate,
    NewAvailable,
    /// <summary>The repository has no published release yet.</summary>
    NoRelease,
    /// <summary>DNS/TLS/timeout: the GitHub API could not be reached at all.</summary>
    Unreachable,
    /// <summary>403/429 from GitHub's unauthenticated rate limit.</summary>
    RateLimited,
    BadResponse,
}

public sealed record UpdateInfo
{
    public UpdateKind Kind { get; init; }
    public string Message { get; init; } = "";
    public string CurrentVersion { get; init; } = "";
    public string? LatestVersion { get; init; }
    public string? ReleaseUrl { get; init; }
    public string? DownloadUrl { get; init; }
    public string? PublishedAt { get; init; }
    public string? Notes { get; init; }
    public bool HasNew => Kind == UpdateKind.NewAvailable;
}

/// <summary>
/// Compares the running build with the newest GitHub Release.
///
/// Only api.github.com is contacted: on this machine github.com and
/// raw.githubusercontent.com are unreachable, while the API host answers in
/// about a second. Opening the human-facing pages is left to the default
/// browser, which has its own network path.
/// </summary>
public static class UpdateChecker
{
    public const string DefaultRepo = "Lanyushun/WINNAS";
    public const string ContactEmail = "3457754861@qq.com";

    private static readonly HttpClient Http = new(new HttpClientHandler { UseProxy = true })
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    static UpdateChecker()
    {
        // GitHub rejects requests without a User-Agent with 403.
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("WINNAS-Console/" + HelpTopics.Version);
        Http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    /// <summary>
    /// Accepts "owner/repo", a full GitHub URL, or a trailing ".git". Anything
    /// else falls back to the default so a bad setting can never forge an API URL.
    /// </summary>
    public static string Repo
    {
        get
        {
            var s = (AppState.Settings.UpdateRepo ?? "").Trim().TrimEnd('/');
            if (s.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
                Uri.TryCreate(s, UriKind.Absolute, out var u) &&
                u.Host.EndsWith("github.com", StringComparison.OrdinalIgnoreCase))
                s = u.AbsolutePath.Trim('/');

            if (s.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
            return Regex.IsMatch(s, @"^[\w.\-]+/[\w.\-]+$") ? s : DefaultRepo;
        }
    }

    public static string RepoUrl => "https://github.com/" + Repo;
    public static string ReleasesUrl => RepoUrl + "/releases";
    public static string MailUrl => "mailto:" + ContactEmail;

    public static string ToolTip =>
        $"项目仓库：{RepoUrl}\n" +
        $"联系方式：{ContactEmail}\n\n" +
        "点击打开仓库主页；旁边的「检查更新」会比对 GitHub 上最新的 Release 版本。";

    public static async Task<UpdateInfo> CheckAsync(CancellationToken ct = default)
    {
        var current = HelpTopics.Version;
        var api = $"https://api.github.com/repos/{Repo}/releases/latest";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, api);
            using var response = await Http.SendAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return Fail(UpdateKind.NoRelease, current,
                    "仓库还没有发布任何 Release，暂时没有可比的版本。");

            if (response.StatusCode is HttpStatusCode.Forbidden or (HttpStatusCode)429)
            {
                var remaining = response.Headers.TryGetValues("X-RateLimit-Remaining", out var v)
                    ? v.FirstOrDefault() : "?";
                return Fail(UpdateKind.RateLimited, current,
                    $"GitHub API 触发速率限制（未认证每小时 60 次，剩余 {remaining}），稍后再试。");
            }

            if (!response.IsSuccessStatusCode)
                return Fail(UpdateKind.BadResponse, current,
                    $"GitHub API 返回 HTTP {(int)response.StatusCode} {response.ReasonPhrase}。");

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tag = Str(root, "tag_name");
            var releaseUrl = Str(root, "html_url") ?? ReleasesUrl;
            var published = Str(root, "published_at");
            var notes = TrimNotes(Str(root, "name"), Str(root, "body"));
            var download = PickAsset(root);

            // A tag that is not a version number still yields a usable link:
            // report the release without pretending we can order the two.
            var cmp = Compare(current, tag);
            if (cmp == int.MinValue)
                return new UpdateInfo
                {
                    Kind = UpdateKind.BadResponse,
                    CurrentVersion = current,
                    LatestVersion = tag,
                    ReleaseUrl = releaseUrl,
                    DownloadUrl = download,
                    PublishedAt = published,
                    Notes = notes,
                    Message = $"仓库最新版本标签是 {tag}，无法与本地 v{current} 比较；请到发布页查看。",
                };

            if (cmp < 0)
                return new UpdateInfo
                {
                    Kind = UpdateKind.NewAvailable,
                    CurrentVersion = current,
                    LatestVersion = tag,
                    ReleaseUrl = releaseUrl,
                    DownloadUrl = download,
                    PublishedAt = published,
                    Notes = notes,
                    Message = $"发现新版本 {tag}（当前 v{current}）{Suffix(published)}",
                };

            var detail = cmp == 0
                ? $"已是最新版本 v{current}{Suffix(published)}"
                : $"已是最新：本地 v{current} 比仓库已发布的 {tag} 还新（远端 Release 尚未跟进）";
            return new UpdateInfo
            {
                Kind = UpdateKind.UpToDate,
                CurrentVersion = current,
                LatestVersion = tag,
                ReleaseUrl = releaseUrl,
                DownloadUrl = download,
                PublishedAt = published,
                Notes = notes,
                Message = detail,
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Fail(UpdateKind.Unreachable, current, "连接 GitHub API 超时（10 秒），可能被网络或代理拦截。");
        }
        catch (HttpRequestException ex)
        {
            return Fail(UpdateKind.Unreachable, current,
                "无法连接 GitHub API：" + Short(ex.Message));
        }
        catch (JsonException)
        {
            return Fail(UpdateKind.BadResponse, current, "GitHub API 返回的内容不是有效 JSON。");
        }
    }

    private static UpdateInfo Fail(UpdateKind kind, string current, string message) => new()
    {
        Kind = kind,
        CurrentVersion = current,
        Message = message,
        ReleaseUrl = ReleasesUrl,
    };

    /// <summary>
    /// Returns negative when the remote tag is newer, 0 when equal, positive
    /// when local is newer, and int.MinValue when either side is unparsable.
    /// </summary>
    public static int Compare(string current, string? remote)
    {
        static Version? Parse(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim().TrimStart('v', 'V');
            var dash = s.IndexOf('-');              // drop pre-release suffix
            if (dash > 0) s = s[..dash];
            return Version.TryParse(s, out var v) ? v : null;
        }

        var a = Parse(current);
        var b = Parse(remote);
        return a is null || b is null ? int.MinValue : a.CompareTo(b);
    }

    private static string? Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static string? PickAsset(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        string? first = null;
        foreach (var a in assets.EnumerateArray())
        {
            var url = Str(a, "browser_download_url");
            if (string.IsNullOrWhiteSpace(url)) continue;
            first ??= url;
            var name = Str(a, "name") ?? "";
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                return url;
        }
        return first;
    }

    private static string? TrimNotes(string? title, string? body)
    {
        var text = string.IsNullOrWhiteSpace(body) ? title : body;
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Replace("\r\n", "\n").Trim();
        var lines = text.Split('\n').Take(8);
        var joined = string.Join("\n", lines);
        return joined.Length > 600 ? joined[..600] + "…" : joined;
    }

    private static string Suffix(string? published) =>
        string.IsNullOrWhiteSpace(published) ? "" : "，发布于 " + published[..Math.Min(10, published.Length)];

    private static string Short(string s)
    {
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length > 160 ? s[..160] + "…" : s;
    }
}
