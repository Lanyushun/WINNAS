using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Winnas.Console.Infrastructure;
using Winnas.Console.Models;
using Winnas.Console.Services;

namespace Winnas.Console.ViewModels;

/// <summary>
/// All application settings in one place.
///
/// The AI credentials used to live inside the chat page, which left roughly a
/// third of the screen permanently occupied by a form nobody touches after the
/// first run. They belong here, next to every other knob.
/// </summary>
public partial class SettingsViewModel : PageViewModel
{
    private readonly Action? _onSaved;
    private readonly DispatcherTimer _debounce;
    private bool _loading;

    public SettingsViewModel(WinnasService service, Action? onSaved = null) : base(service)
    {
        _onSaved = onSaved;

        // A save on every keystroke would rewrite config.json dozens of times
        // while someone types a URL. Debounce it.
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Persist(); };

        // Loading the stored key must not count as a user edit, otherwise the
        // constructor would schedule a pointless config write.
        _loading = true;
        ApiKeyInput = SecretVault.UnprotectText(AppState.Settings.AiApiKey);
        WakeMac = CurrentProfile?.MacAddress ?? "";
        _loading = false;
    }

    public override string Title => "设置";
    public override string Glyph => "⚙";
    public override string Group => "系统";

    public override bool AutoRefreshable => false;

    public override string HelpText =>
        "这里管的是「这台控制台」自己的行为：界面皮肤、刷新节奏、日志、AI 接入，以及默认作用在哪台被控端。" +
        "改动会自动保存，不需要点保存按钮。AI 的 Key 用 Windows DPAPI 按当前用户加密，换用户或换机器都读不出来。";

    // --------------------------------------------------------------- 外观

    /// <summary>Six palettes, each previewing itself with its own colours.</summary>
    public IReadOnlyList<Skin> Skins => ThemeManager.Skins;

    public Skin SelectedSkin
    {
        get => ThemeManager.Current;
        set
        {
            if (value is null || value.Key == ThemeManager.CurrentKey) return;

            ThemeManager.Apply(value.Key);
            AppState.Settings.Theme = value.Key;
            AppState.Save();

            OnPropertyChanged();
            ActivityLog.Info("外观", $"界面已切换为「{value.Name}」");
        }
    }

    // --------------------------------------------------------------- 通用

    public List<int> RefreshChoices { get; } = new() { 5, 10, 15, 30, 60, 120 };

    public int RefreshSeconds
    {
        get => AppState.Settings.RefreshSeconds;
        set
        {
            if (value is < 5 or > 600 || value == AppState.Settings.RefreshSeconds) return;
            AppState.Settings.RefreshSeconds = value;
            OnPropertyChanged();
        }
    }

    public bool ShowPageHelp
    {
        get => AppState.Settings.ShowPageHelp;
        set
        {
            if (value == AppState.Settings.ShowPageHelp) return;
            AppState.Settings.ShowPageHelp = value;
            OnPropertyChanged();
        }
    }

    public int LogKeepDays
    {
        get => AppState.Settings.LogKeepDays;
        set
        {
            var clamped = Math.Clamp(value, 1, 365);
            if (clamped == AppState.Settings.LogKeepDays) return;
            AppState.Settings.LogKeepDays = clamped;
            OnPropertyChanged();
        }
    }

    public string WatchedServicesText
    {
        get => string.Join(", ", AppState.Settings.WatchedServices);
        set
        {
            var list = value
                .Split(new[] { ',', '，', ';', '；', ' ', '\t', '\n', '\r' },
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (list.SequenceEqual(AppState.Settings.WatchedServices)) return;
            AppState.Settings.WatchedServices = list;
            OnPropertyChanged();
        }
    }

    // ----------------------------------------------------------- AI 助手

    public ObservableCollection<string> Models { get; } = new();

    [ObservableProperty] private string apiKeyInput = "";

    [ObservableProperty] private string? modelNote;

    [ObservableProperty] private string? aiStatus;

    [ObservableProperty] private bool testing;

    public string AiBaseUrl
    {
        get => AppState.Settings.AiBaseUrl;
        set
        {
            var text = value.Trim();
            if (text == AppState.Settings.AiBaseUrl) return;
            AppState.Settings.AiBaseUrl = text;
            OnPropertyChanged();
        }
    }

    public string AiModel
    {
        get => AppState.Settings.AiModel;
        set
        {
            var text = value.Trim();
            if (text == AppState.Settings.AiModel) return;
            AppState.Settings.AiModel = text;
            OnPropertyChanged();
        }
    }

    public double AiTemperature
    {
        get => AppState.Settings.AiTemperature;
        set
        {
            if (Math.Abs(value - AppState.Settings.AiTemperature) < 0.001) return;
            AppState.Settings.AiTemperature = value;
            OnPropertyChanged();
        }
    }

    public string AiSystemPrompt
    {
        get => AppState.Settings.AiSystemPrompt;
        set
        {
            if (value == AppState.Settings.AiSystemPrompt) return;
            AppState.Settings.AiSystemPrompt = value;
            OnPropertyChanged();
        }
    }

    public bool AiAttachContext
    {
        get => AppState.Settings.AiAttachContext;
        set
        {
            if (value == AppState.Settings.AiAttachContext) return;
            AppState.Settings.AiAttachContext = value;
            OnPropertyChanged();
        }
    }

    public bool KeyConfigured => AppState.Settings.AiApiKey.Length > 0;

    /// <summary>
    /// The key is only shown as plain text while it is being edited. A saved
    /// key stays masked so it does not end up in a screenshot or a screen share.
    /// </summary>
    [ObservableProperty] private bool keyEditing;

    public string MaskedKey
    {
        get
        {
            var key = ApiKeyInput.Trim();
            if (key.Length == 0) return "未填写";
            if (key.Length <= 10) return new string('•', key.Length);
            return $"{key[..4]}{new string('•', 8)}{key[^4..]}";
        }
    }

    [RelayCommand]
    private void EditKey() => KeyEditing = true;

    public string KeyStateText => KeyConfigured
        ? "已保存（DPAPI 加密，仅当前 Windows 用户可解）"
        : "尚未配置 API Key";

    partial void OnApiKeyInputChanged(string value)
    {
        // Storing the key needs DPAPI, so it is committed on save rather than
        // on every character.
        OnPropertyChanged(nameof(MaskedKey));
        ScheduleSave();
    }

    [RelayCommand]
    private void SaveApiKey()
    {
        var typed = ApiKeyInput.Trim();
        AppState.Settings.AiApiKey = typed.Length == 0 ? "" : SecretVault.ProtectText(typed);

        ApiKeyInput = typed.Length == 0 ? "" : typed;
        AppState.Save();
        KeyEditing = false;
        OnPropertyChanged(nameof(MaskedKey));
        OnPropertyChanged(nameof(KeyConfigured));
        OnPropertyChanged(nameof(KeyStateText));

        AiStatus = typed.Length == 0 ? "已清除 API Key" : "API Key 已保存";
        ActivityLog.Info("设置", AiStatus);
    }

    [RelayCommand]
    private async Task FetchModels()
    {
        if (!KeyConfigured) { AiStatus = "先保存 API Key 再获取模型列表"; return; }

        Testing = true;
        try
        {
            var models = await AiService.ListModelsAsync();
            if (models.Count == 0)
            {
                AiStatus = "接口没有返回模型列表，请手动填写模型名";
                ModelNote = null;
                return;
            }

            Models.Clear();
            foreach (var m in models) Models.Add(m);
            ModelNote = $"接口返回 {models.Count} 个模型";
            AiStatus = "连接正常";
        }
        catch (Exception ex)
        {
            AiStatus = "取模型列表失败：" + OpResult.From(ex).Message;
        }
        finally { Testing = false; }
    }

    /// <summary>A real round trip, not just "is the field non-empty".</summary>
    [RelayCommand]
    private async Task TestConnection()
    {
        if (!KeyConfigured) { AiStatus = "先保存 API Key"; return; }

        Testing = true;
        AiStatus = "正在请求…";
        try
        {
            var payload = new List<ChatMessage>
            {
                new() { Role = "system", Content = "只回答两个字：可用" },
                new() { Role = "user", Content = "测试" },
            };

            var text = "";
            await foreach (var chunk in AiService.StreamAsync(payload, AiModel, 0, CancellationToken.None))
                text += chunk;

            AiStatus = $"连接正常 · 模型 {AiModel} 回了 {text.Trim().Length} 个字符";
            ActivityLog.Ok("设置", "AI 连接测试成功", AiModel);
        }
        catch (Exception ex)
        {
            AiStatus = "连接失败：" + OpResult.From(ex).Message;
            ActivityLog.Error("设置", "AI 连接测试失败", OpResult.From(ex).Message);
        }
        finally { Testing = false; }
    }

    [RelayCommand]
    private void ResetPrompt()
    {
        AiSystemPrompt = AppSettings.DefaultAiSystemPrompt;
        AiStatus = "已恢复默认系统提示词";
    }

    // --------------------------------------------------------- 被控端

    [ObservableProperty] private string wakeMac = "";

    public string BackupPlanLogDir
    {
        get => AppState.Settings.BackupPlanLogDir;
        set
        {
            var text = value.Trim();
            if (text.Length == 0 || text == AppState.Settings.BackupPlanLogDir) return;
            AppState.Settings.BackupPlanLogDir = text;
            OnPropertyChanged();
        }
    }

    public string RemoteDir => RemoteControlService.RemoteDir;

    public string TargetText => CurrentSession is null
        ? "未连接"
        : $"{CurrentSession.Label} · {CurrentSession.Profile.TransportText}";

    public string TargetExplain =>
        "控制台运行在这台台式机上，但采集与控制的对象始终是「被控端」。" +
        "CPU、内存、磁盘、共享、备份以及远控页的关机/重启/命令，全部在被控端执行；" +
        "本机只负责显示和下发指令。";

    [RelayCommand]
    private void SaveWakeMac()
    {
        if (CurrentProfile is not { } profile)
        {
            Notice = "先连接一台被控端，MAC 会存在那条连接里。";
            return;
        }

        profile.MacAddress = WakeMac.Trim();
        AppState.Save();
        Notice = $"已把 {profile.MacAddress} 保存到连接「{profile.Name}」";
    }

    private ConnectionProfile? CurrentProfile =>
        CurrentSession is null ? null : AppState.Find(CurrentSession.Profile.Id);

    // ---------------------------------------------------------- 关于

    public string VersionText => "v" + HelpTopics.Version;
    public string RuntimeText => ".NET " + Environment.Version;
    public string ExePath => Environment.ProcessPath ?? "—";
    public string ConfigPath => AppState.FilePath;
    public string LogDir => ActivityLog.Dir;

    [ObservableProperty] private string? resultText;

    [RelayCommand]
    private void OpenConfigDir() => Shell.OpenFolder(AppState.Dir);

    [RelayCommand]
    private void OpenLogDir() => Shell.OpenFolder(ActivityLog.Dir);

    [RelayCommand]
    private void ExportConfig() => Shell.OpenFolder(AppState.Dir);

    // ------------------------------------------------------------ plumbing

    /// <summary>Mirror of AppSettings, re-raised whenever a page needs a fresh look.</summary>
    public void Reload()
    {
        OnPropertyChanged(nameof(SelectedSkin));
        OnPropertyChanged(nameof(RefreshSeconds));
        OnPropertyChanged(nameof(ShowPageHelp));
        OnPropertyChanged(nameof(LogKeepDays));
        OnPropertyChanged(nameof(WatchedServicesText));
        OnPropertyChanged(nameof(AiBaseUrl));
        OnPropertyChanged(nameof(AiModel));
        OnPropertyChanged(nameof(AiTemperature));
        OnPropertyChanged(nameof(AiSystemPrompt));
        OnPropertyChanged(nameof(AiAttachContext));
        OnPropertyChanged(nameof(KeyConfigured));
        OnPropertyChanged(nameof(KeyStateText));
        OnPropertyChanged(nameof(BackupPlanLogDir));
        OnPropertyChanged(nameof(TargetText));
    }

    public override Task LoadAsync(WinnasSession? session)
    {
        OnPropertyChanged(nameof(TargetText));
        OnPropertyChanged(nameof(KeyConfigured));
        OnPropertyChanged(nameof(KeyStateText));
        return Task.CompletedTask;
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading) return;

        // Only settings-backed properties should trigger a disk write.
        if (e.PropertyName is nameof(ApiKeyInput) or nameof(RefreshSeconds) or nameof(ShowPageHelp)
            or nameof(LogKeepDays) or nameof(WatchedServicesText) or nameof(AiBaseUrl)
            or nameof(AiModel) or nameof(AiTemperature) or nameof(AiSystemPrompt)
            or nameof(AiAttachContext) or nameof(BackupPlanLogDir))
            ScheduleSave();
    }

    private void ScheduleSave()
    {
        if (_loading) return;
        _debounce.Stop();
        _debounce.Start();
    }

    private void Persist()
    {
        try
        {
            AppState.Save();
            _onSaved?.Invoke();
        }
        catch (Exception ex)
        {
            ResultText = "保存失败：" + OpResult.From(ex).Message;
        }
    }
}
