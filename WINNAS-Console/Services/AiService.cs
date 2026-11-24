using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Winnas.Console.Infrastructure;

namespace Winnas.Console.Services;

/// <summary>
/// Talks to any OpenAI-compatible chat completions endpoint. DeepSeek, Moonshot,
/// OpenAI, Azure-hosted proxies and local Ollama-with-compat all speak this
/// shape, so one client covers all of them and the user can switch by editing
/// two fields.
/// </summary>
public static class AiService
{
    // Streaming responses are open-ended by nature, so no HttpClient timeout;
    // cancellation is the caller's job.
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>
    /// Users paste either "https://api.deepseek.com" or ".../v1". Both work as
    /// long as we only append what is missing.
    /// </summary>
    public static string NormalizeEndpoint(string baseUrl)
    {
        var b = (baseUrl ?? "").Trim().TrimEnd('/');
        if (b.Length == 0) return "";
        if (b.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) return b;
        if (!b.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) b += "/v1";
        return b + "/chat/completions";
    }

    public static string NormalizeModelsEndpoint(string baseUrl)
    {
        var chat = NormalizeEndpoint(baseUrl);
        if (chat.Length == 0) return "";
        return chat[..^"/chat/completions".Length] + "/models";
    }

    /// <summary>Yields reply text as it arrives, one delta at a time.</summary>
    public static async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<Models.ChatMessage> messages,
        string model,
        double temperature,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var key = SecretVault.UnprotectText(AppState.Settings.AiApiKey);
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("尚未配置 API Key，请先在下方填写并保存");

        var url = NormalizeEndpoint(AppState.Settings.AiBaseUrl);
        if (url.Length == 0)
            throw new InvalidOperationException("尚未配置 API 地址");

        var payload = new ChatRequest
        {
            Model = string.IsNullOrWhiteSpace(model) ? AppState.Settings.AiModel : model,
            Temperature = temperature,
            Stream = true,
            Messages = messages.Select(m => new ChatRequest.Message { Role = m.Role, Content = m.Content }).ToList(),
        };

        var json = JsonSerializer.Serialize(payload, JsonOpts);

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!resp.IsSuccessStatusCode)
        {
            var body = "";
            try { body = await resp.Content.ReadAsStringAsync(ct); } catch { /* ignore */ }
            throw new InvalidOperationException(DescribeFailure(resp.StatusCode, body));
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        // ReadLineAsync returning null is the end of stream; testing
        // EndOfStream first is a blocking call in disguise (CA2024).
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var data = line["data:".Length..].Trim();
            if (data.Length == 0) continue;
            if (data.Equals("[DONE]", StringComparison.Ordinal)) break;

            var delta = ExtractDelta(data);
            if (delta.Length > 0) yield return delta;
        }
    }

    private static string ExtractDelta(string data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            if (!doc.RootElement.TryGetProperty("choices", out var choices)) return "";
            if (choices.GetArrayLength() == 0) return "";

            var first = choices[0];
            if (first.TryGetProperty("delta", out var delta) &&
                delta.TryGetProperty("content", out var content))
                return content.GetString() ?? "";

            // Some proxies send the full message instead of a delta.
            if (first.TryGetProperty("message", out var msg) &&
                msg.TryGetProperty("content", out var c2))
                return c2.GetString() ?? "";
        }
        catch (JsonException) { /* a partial frame is not fatal */ }

        return "";
    }

    private static string DescribeFailure(System.Net.HttpStatusCode code, string body)
    {
        var detail = "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                if (err.ValueKind == JsonValueKind.String) detail = err.GetString() ?? "";
                else if (err.TryGetProperty("message", out var m)) detail = m.GetString() ?? "";
            }
            else if (doc.RootElement.TryGetProperty("message", out var m2)) detail = m2.GetString() ?? "";
        }
        catch { /* plain-text errors fall through */ }

        if (detail.Length == 0) detail = body.Length > 160 ? body[..160] : body;

        var codeText = (int)code;
        var hint = codeText switch
        {
            401 or 403 => "（API Key 无效或没有权限）",
            404 => "（地址不对，检查 API Base 是否需要 /v1 后缀）",
            429 => "（额度或速率受限，稍后再试）",
            >= 500 => "（服务端故障）",
            _ => "",
        };

        return $"请求失败 {codeText}{hint} {detail}".Trim();
    }

    /// <summary>Lists models the key can see. Empty when the endpoint hides them.</summary>
    public static async Task<List<string>> ListModelsAsync(CancellationToken ct = default)
    {
        var key = SecretVault.UnprotectText(AppState.Settings.AiApiKey);
        var url = NormalizeModelsEndpoint(AppState.Settings.AiBaseUrl);
        if (key.Length == 0 || url.Length == 0) return new List<string>();

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return new List<string>();

            var text = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(text);

            var result = new List<string>();
            if (doc.RootElement.TryGetProperty("data", out var data))
                foreach (var item in data.EnumerateArray())
                    if (item.TryGetProperty("id", out var id))
                        result.Add(id.GetString() ?? "");

            return result.Where(x => x.Length > 0).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch { return new List<string>(); }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Without this every Chinese character is sent as \uXXXX — six bytes
        // instead of three, on a context brief that is almost all Chinese.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private sealed class ChatRequest
    {
        [JsonPropertyName("model")] public string Model { get; set; } = "";
        [JsonPropertyName("messages")] public List<Message> Messages { get; set; } = new();
        [JsonPropertyName("temperature")] public double Temperature { get; set; }
        [JsonPropertyName("stream")] public bool Stream { get; set; }

        public sealed class Message
        {
            [JsonPropertyName("role")] public string Role { get; set; } = "user";
            [JsonPropertyName("content")] public string Content { get; set; } = "";
        }
    }
}
