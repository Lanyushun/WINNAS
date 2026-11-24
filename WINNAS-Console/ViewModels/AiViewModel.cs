using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Winnas.Console.Infrastructure;
using Winnas.Console.Models;
using Winnas.Console.Services;

namespace Winnas.Console.ViewModels;

/// <summary>
/// The AI page is the conversation and nothing else.
///
/// Model, key and prompt settings live on the Settings page: a chat window
/// permanently cut in half by a form is not a chat window. The page still
/// tells you which model answers and whether live context is attached, and
/// offers a one-click jump to the settings when the key is missing.
/// </summary>
public partial class AiViewModel : PageViewModel
{
    private const int HistoryLimit = 20;

    private CancellationTokenSource? _cts;
    private int _chunkCount;

    /// <summary>View listens to this to keep the newest text in sight.</summary>
    public event EventHandler? ScrollRequested;

    /// <summary>Set by MainViewModel: navigates to the Settings page.</summary>
    public Action? SettingsRequested;

    public ObservableCollection<ChatMessage> Messages { get; } = new();

    public ObservableCollection<string> QuickPrompts { get; } = new()
    {
        "根据当前磁盘健康数据，哪块盘最需要关注？给出判断依据。",
        "卷容量快满了，给一份按风险排序的清理建议。",
        "备份上次没成功，结合当前状态分析可能的原因和排查顺序。",
        "我要在这台 NAS 上新增一个只读共享，给出具体步骤和命令。",
        "这台机器现在有什么值得担心的隐患？按严重程度排序。",
    };

    [ObservableProperty] private string inputText = "";

    [ObservableProperty] private bool streaming;

    [ObservableProperty] private string? contextNote;

    public AiViewModel(WinnasService service) : base(service) { }

    public override string Title => "AI 助手";
    public override string Glyph => "✦";
    public override string Group => "智能";

    public override string HelpText =>
        "整个页面就是对话区。模型、API Key、温度与提示词都在「设置 → AI 助手」里。" +
        "勾选了附带实时状态时，每次提问都会把被控端的磁盘、卷、共享、快照与备份状态注入 system，" +
        "所以可以直接问「哪块盘最需要关注」，而不是得到通用建议。";

    public bool KeyConfigured => AppState.Settings.AiApiKey.Length > 0;

    public string ModelText => string.IsNullOrWhiteSpace(AppState.Settings.AiModel)
        ? "未选择模型"
        : AppState.Settings.AiModel;

    public string ContextText => AppState.Settings.AiAttachContext
        ? "附带被控端实时状态"
        : "仅对话，不附带状态";

    public string TargetText => CurrentSession is null
        ? "未连接被控端"
        : $"被控端 {CurrentSession.Profile.Target}";

    public bool HasMessages => Messages.Count > 0;

    public override Task LoadAsync(WinnasSession? session)
    {
        OnPropertyChanged(nameof(KeyConfigured));
        OnPropertyChanged(nameof(ModelText));
        OnPropertyChanged(nameof(ContextText));
        OnPropertyChanged(nameof(TargetText));
        return Task.CompletedTask;
    }

    // --------------------------------------------------------------- chat

    [RelayCommand]
    private async Task Send()
    {
        var text = InputText.Trim();
        if (text.Length == 0 || Streaming) return;

        if (!KeyConfigured)
        {
            Error = "还没配置 API Key。点右上角「AI 设置」填一个，再回来提问。";
            SettingsRequested?.Invoke();
            return;
        }

        InputText = "";
        Error = null;

        Messages.Add(new ChatMessage { Role = "user", Content = text });
        var reply = new ChatMessage { Role = "assistant", Streaming = true };
        Messages.Add(reply);
        OnPropertyChanged(nameof(HasMessages));
        ScrollRequested?.Invoke(this, EventArgs.Empty);

        Streaming = true;
        _cts = new CancellationTokenSource();
        var buffer = new StringBuilder();

        try
        {
            var payload = await BuildPayloadAsync(reply);

            await foreach (var chunk in AiService.StreamAsync(payload, AppState.Settings.AiModel,
                               AppState.Settings.AiTemperature, _cts.Token))
            {
                buffer.Append(chunk);
                reply.Content = buffer.ToString();

                // Scrolling on every delta would fight the user's own scrolling.
                if (++_chunkCount % 8 == 0) ScrollRequested?.Invoke(this, EventArgs.Empty);
            }

            if (buffer.Length == 0) reply.Content = "（模型没有返回任何内容）";
        }
        catch (OperationCanceledException)
        {
            reply.Content = buffer.Length == 0 ? "（已停止）" : buffer + "\n\n（已停止）";
        }
        catch (Exception ex)
        {
            var msg = OpResult.From(ex).Message;
            reply.Content = "请求失败：" + msg;
            Error = msg;
            ActivityLog.Error("AI", "模型请求失败", msg);
        }
        finally
        {
            reply.Streaming = false;
            Streaming = false;
            _cts = null;
        }
    }

    [RelayCommand]
    private void Stop()
    {
        _cts?.Cancel();
        Streaming = false;
    }

    [RelayCommand]
    private void Clear()
    {
        Messages.Clear();
        ContextNote = null;
        OnPropertyChanged(nameof(HasMessages));
    }

    [RelayCommand]
    private void UsePrompt(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return;
        InputText = prompt;
    }

    [RelayCommand]
    private void OpenSettings()
    {
        SettingsRequested?.Invoke();
        RefreshSettings();
    }

    [RelayCommand]
    private void CopyMessage(ChatMessage? message)
    {
        if (message is null || message.Content.Length == 0) return;
        try
        {
            System.Windows.Clipboard.SetText(message.Content);
            Notice = "回答已复制到剪贴板";
        }
        catch (Exception ex)
        {
            // Clipboard contention is common and harmless; do not shout about it.
            ActivityLog.Warn("AI", "复制失败", OpResult.From(ex).Message);
        }
    }

    /// <summary>Called when the user comes back from the settings page.</summary>
    public void RefreshSettings()
    {
        OnPropertyChanged(nameof(KeyConfigured));
        OnPropertyChanged(nameof(ModelText));
        OnPropertyChanged(nameof(ContextText));
    }

    private async Task<List<ChatMessage>> BuildPayloadAsync(ChatMessage pendingReply)
    {
        var settings = AppState.Settings;
        var system = settings.AiSystemPrompt;

        if (settings.AiAttachContext && CurrentSession is not null)
        {
            var brief = await NasContext.BuildAsync(CurrentSession, Service);
            system += "\n\n下面是这台机器当前的真实运行状态（由本程序实时采集），" +
                      "回答与它相关的问题时以此为准，不要凭空猜测：\n\n" + brief;
            ContextNote = $"本次附带了 {brief.Length} 字符的实时状态";
        }
        else
        {
            ContextNote = null;
        }

        var payload = new List<ChatMessage> { new() { Role = "system", Content = system } };

        var history = Messages
            .Where(m => m != pendingReply && m.Content.Length > 0)
            .TakeLast(HistoryLimit)
            .ToList();

        payload.AddRange(history);
        return payload;
    }
}
