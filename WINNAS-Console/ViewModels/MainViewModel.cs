using System.Collections.ObjectModel;
using System.Security.Principal;
using System.Text;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Winnas.Console.Infrastructure;
using Winnas.Console.Models;
using Winnas.Console.Services;

namespace Winnas.Console.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly WinnasService _service = new();
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _toastTimer;
    private WinnasSession? _session;

    public ObservableCollection<PageViewModel> Pages { get; } = new();

    [ObservableProperty] private PageViewModel? currentPage;

    [ObservableProperty] private ConnectionProfile? currentProfile;

    [ObservableProperty] private bool isConnecting;

    [ObservableProperty] private string statusText = "未连接";

    [ObservableProperty] private bool autoRefresh = true;

    [ObservableProperty] private string? lastUpdateText;

    [ObservableProperty] private bool isAdmin;

    [ObservableProperty] private string? toastText;

    [ObservableProperty] private bool toastIsError;

    [ObservableProperty] private bool toastVisible;

    [ObservableProperty] private bool isCheckingUpdate;

    /// <summary>Short "· 已是最新 / · 有新版本" marker in the status bar.</summary>
    [ObservableProperty] private string? updateHintText;

    public string VersionText => "v" + HelpTopics.Version;

    // ---------------------------------------------------------- GitHub / update

    public string GitHubLabel => "GitHub · " + UpdateChecker.Repo;
    public string GitHubTip => UpdateChecker.ToolTip;
    public string ContactEmail => UpdateChecker.ContactEmail;
    public string CheckUpdateButtonText => IsCheckingUpdate ? "检查中…" : "检查更新";

    public MainViewModel()
    {
        // The reload callback runs later, after the field is assigned; the null
        // forgiving operator keeps the compiler honest about that ordering.
        // Settings is built first so the connection page can jump to it when
        // the AI diagnosis has no API key to work with.
        var settingsPage = new SettingsViewModel(_service, OnSettingsSaved);

        _connections = new ConnectionsViewModel(_service, ConnectAsync,
            () => Disconnect(),
            () => _connections!.LoadProfiles(),
            () => CurrentPage = settingsPage);

        // Without this the page opened with an empty list: the loader only ran
        // after saving or deleting a connection.
        _connections.LoadProfiles();
        var connections = _connections;

        var aiPage = new AiViewModel(_service);

        // Monitoring first, then the things you act on, then the app's own
        // configuration. Ctrl+1..9 covers the first nine; 0 and Shift+1..2 the rest.
        Pages.Add(new OverviewViewModel(_service));
        Pages.Add(new StorageViewModel(_service));
        Pages.Add(new SystemViewModel(_service));
        Pages.Add(new SharesViewModel(_service));
        Pages.Add(new FilesViewModel(_service));
        Pages.Add(new BackupViewModel(_service, OnSettingsSaved));
        Pages.Add(new SnapshotsViewModel(_service));
        Pages.Add(new RemoteViewModel(_service));
        Pages.Add(aiPage);
        Pages.Add(new HelpViewModel(_service));
        Pages.Add(settingsPage);
        Pages.Add(connections);
        // Appended, not inserted: the Ctrl+N shortcuts and the saved
        // LastPageTitle are positional, and moving an existing page would
        // silently retarget someone's muscle memory.
        Pages.Add(new SyncViewModel(_service));

        Settings = settingsPage;
        aiPage.SettingsRequested = () => CurrentPage = settingsPage;

        // Pages issue commands (create share, snapshot, run task) and need the
        // live session without owning it.
        foreach (var page in Pages) page.SessionProvider = () => _session;

        MarkGroupHeaders();

        // Reopen whichever page was last used; a monitoring tool that always
        // jumps back to Overview wastes a click every single launch.
        var lastTitle = AppState.Settings.LastPageTitle;
        CurrentPage = Pages.FirstOrDefault(p => p.Title == lastTitle) ?? Pages[0];

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Clamp(AppState.Settings.RefreshSeconds)) };
        _timer.Tick += OnTick;

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _toastTimer.Tick += (_, _) => HideToast();

        IsAdmin = IsElevated();
        ActivityLog.Info("启动", $"WINNAS 控制台已启动（{VersionText}）",
            IsAdmin ? "以管理员权限运行" : "普通权限：写操作会失败");

        // Opt-in and fire-and-forget: a slow or blocked GitHub must never hold
        // up the window, and a routine "no update" must not produce a popup.
        _ = AutoCheckUpdateAsync();
    }

    private ConnectionsViewModel? _connections;

    public ConnectionsViewModel Connections => _connections!;

    /// <summary>Exposed so the header menu can jump straight to the settings page.</summary>
    public SettingsViewModel Settings { get; }

    /// <summary>
    /// Says, in one line, which machine the numbers on screen belong to. The
    /// console runs on the desktop; everything shown is the managed machine.
    /// </summary>
    /// <summary>
    /// Prefers the name the target reports for itself over whatever the user
    /// typed into the connection, so the header cannot claim to manage one
    /// machine while reading another.
    /// </summary>
    public string ControlledLabel => _session is null
        ? "未连接被控端"
        : _session.IsRemote
            ? $"被控端 · {Show(_session.MachineName, _session.Profile.Name)}（{_session.Profile.Target}）"
            : $"被控端 · 本机 {_session.MachineName}";

    private static string Show(string actual, string fallback) =>
        actual.Length > 0 ? actual : fallback;

    public bool Connected => _session is not null;

    private static int Clamp(int seconds) => seconds is < 5 or > 600 ? 10 : seconds;

    /// <summary>True when the process can perform the privileged CIM writes.</summary>
    private static bool IsElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>Sidebar groups are visual only, so compute them once up front.</summary>
    private void MarkGroupHeaders()
    {
        string? last = null;
        foreach (var p in Pages)
        {
            p.ShowGroupHeader = p.Group != last;
            last = p.Group;
        }
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        if (!AutoRefresh || CurrentPage is null || !CurrentPage.AutoRefreshable) return;

        try
        {
            // A background refresh must never take the process down: this runs
            // on the dispatcher, so an escaping exception is fatal.
            await CurrentPage.LoadAsync(_session);
            LastUpdateText = DateTime.Now.ToString("HH:mm:ss");
        }
        catch (Exception ex)
        {
            ActivityLog.Error("刷新", "自动刷新失败", OpResult.From(ex).Message);
        }
    }

    partial void OnAutoRefreshChanged(bool value) => _timer.IsEnabled = value && Connected;

    partial void OnCurrentPageChanged(PageViewModel? value)
    {
        // Only the visible page may keep sampling; leaving a hidden page's
        // timer running would query the target for nothing.
        foreach (var page in Pages) (page as ILivePage)?.StopLive();
        if (value is null) return;

        AppState.Settings.LastPageTitle = value.Title;
        AppState.Save();

        _ = SafeLoadAsync(value);
        if (_session is not null) (value as ILivePage)?.StartLive();
    }

    /// <summary>Page loads triggered by navigation must not throw into the dispatcher.</summary>
    private async Task SafeLoadAsync(PageViewModel page)
    {
        try
        {
            await page.LoadAsync(_session);
            LastUpdateText = DateTime.Now.ToString("HH:mm:ss");
        }
        catch (Exception ex)
        {
            page.Error = OpResult.From(ex).Message;
            ActivityLog.Error("页面", $"{page.Title} 加载失败", OpResult.From(ex).Message);
        }
    }

    partial void OnCurrentProfileChanged(ConnectionProfile? value)
    {
        if (value is not null) AppState.Settings.LastConnectionId = value.Id;
    }

    public async Task StartupAsync()
    {
        await ConnectAsync(AppState.Current());

        // Nudge a brand-new install towards the docs once, then never again.
        if (!AppState.Settings.FirstRunDone)
        {
            AppState.Settings.FirstRunDone = true;
            AppState.Save();
            ShowToast("首次使用？左侧「日志与帮助」页有完整上手指南、远程配置命令与错误码排查表");
        }
    }

    public void Shutdown()
    {
        foreach (var page in Pages) (page as ILivePage)?.StopLive();
        Disconnect();
        ActivityLog.Info("退出", "控制台已关闭");
        ActivityLog.Shutdown();
    }

    public async Task ConnectAsync(ConnectionProfile profile)
    {
        if (IsConnecting) return;

        if (profile.IsMisconfigured)
        {
            var msg = $"连接「{profile.Name}」选了 {profile.TransportText}，但没有填主机名；" +
                      "若就这样连过去，管理的会是这台电脑而不是 NAS。请补上地址再连。";
            StatusText = msg;
            ShowToast(msg, true);
            ActivityLog.Warn("连接", "连接被拦下：缺少主机名", profile.Name);
            return;
        }

        IsConnecting = true;
        StatusText = profile.IsLocal ? "正在连接本机..." : $"正在连接 {profile.Target}...";

        try
        {
            // Opening a remote CIM session can block for its full timeout; do it
            // off the UI thread so the window stays responsive while it fails.
            var opened = await Task.Run(() => WinnasSession.Open(profile))
                         ?? throw new InvalidOperationException("连接失败");

            _session?.Dispose();
            _session = opened;
            CurrentProfile = profile;
            OnPropertyChanged(nameof(Connected));
            OnPropertyChanged(nameof(ControlledLabel));
            Settings.Reload();

            StatusText = opened.IsRemote
                ? $"已连接 {opened.Profile.Target}（{opened.Profile.TransportText}）"
                : $"已连接本机 · {Environment.MachineName}";

            ActivityLog.Ok("连接", StatusText, opened.IsRemote ? opened.Profile.TransportText : null);
            ShowToast(StatusText);

            await SafeLoadAsync(CurrentPage!);
            (CurrentPage as ILivePage)?.StartLive();

            // Post-connect maintenance must not hold up the pages: it runs in
            // the background and only speaks when it changed something.
            if (opened.IsRemote) _ = RunPostConnectAsync(opened);
        }
        catch (Exception ex)
        {
            var msg = OpResult.From(ex).Message;
            StatusText = "连接失败";
            if (CurrentPage is not null) CurrentPage.Error = msg;

            ActivityLog.Error("连接", $"连接 {profile.Target} 失败", msg);
            ShowToast("连接失败：" + msg, true);
        }
        finally
        {
            IsConnecting = false;
            _timer.IsEnabled = AutoRefresh && Connected;
        }
    }

    /// <summary>
    /// Post-connect maintenance: clock check plus the optional settings push.
    /// Runs in the background on purpose — the connection is already usable,
    /// and a slow NAS must not delay the pages. Only a correction or a failure
    /// is toasted; an "everything fine" toast on every connect is noise.
    /// </summary>
    private async Task RunPostConnectAsync(WinnasSession session)
    {
        try
        {
            var (corrected, time) = await TimeSync.AutoAsync(session, session.Profile);
            if (!time.Ok) ShowToast("自动校时：" + time.Message, true);
            else if (corrected) ShowToast(time.Message);

            if (AppState.Settings.SyncAutoPush)
            {
                var push = await SettingsSync.PushAsync(session);
                ActivityLog.Write(push.Ok ? LogLevel.Ok : LogLevel.Error,
                    "同步", "连接后自动下发设置", push.Message);
                if (!push.Ok) ShowToast("自动下发设置失败：" + push.Message, true);
            }
        }
        catch (Exception ex)
        {
            ActivityLog.Error("同步", "连接后自动任务异常", OpResult.From(ex).Message);
        }
    }

    public void Disconnect()
    {
        if (_session is null) return;
        var name = _session.Profile.Target;
        _timer.IsEnabled = false;
        foreach (var page in Pages) (page as ILivePage)?.StopLive();
        _session?.Dispose();
        _session = null;
        StatusText = "未连接";
        LastUpdateText = null;
        OnPropertyChanged(nameof(Connected));
        OnPropertyChanged(nameof(ControlledLabel));

        ActivityLog.Info("连接", $"已断开 {name}");
    }

    public void ReloadConnections() => _connections?.LoadProfiles();

    private void OnSettingsSaved()
    {
        ApplyRefreshInterval();

        // The help-strip switch and the AI model badge both read AppSettings
        // directly, so anything already on screen has to be told again.
        foreach (var p in Pages) p.RefreshHelpVisible();
        if (Pages.FirstOrDefault(p => p is AiViewModel) is AiViewModel ai) ai.RefreshSettings();
    }

    public void ApplyRefreshInterval()
    {
        _timer.Interval = TimeSpan.FromSeconds(Clamp(AppState.Settings.RefreshSeconds));
    }

    /// <summary>Refresh the visible page right now, dropping the cache.</summary>
    [RelayCommand]
    private async Task Refresh()
    {
        if (CurrentPage is null) return;
        await RefreshAll();
    }

    [RelayCommand]
    private void DisconnectNow() => Disconnect();

    public async Task RefreshAll()
    {
        if (CurrentPage is null) return;
        _session?.Cache.Invalidate();
        await SafeLoadAsync(CurrentPage);
    }

    // ------------------------------------------------------------- toast

    public void ShowToast(string text, bool isError = false)
    {
        ToastText = text;
        ToastIsError = isError;
        ToastVisible = true;

        _toastTimer.Stop();
        _toastTimer.Start();
    }

    [RelayCommand]
    private void HideToast() => ToastVisible = false;

    // ------------------------------------------------------- GitHub / update

    [RelayCommand]
    private void OpenGitHub() => ShowToast(Shell.OpenUrl(UpdateChecker.RepoUrl).Message);

    [RelayCommand]
    private void OpenReleases() => ShowToast(Shell.OpenUrl(UpdateChecker.ReleasesUrl).Message);

    [RelayCommand]
    private void ContactAuthor() => ShowToast(Shell.OpenUrl(UpdateChecker.MailUrl).Message);

    /// <summary>Manual check: always shows the outcome, and offers the download page.</summary>
    [RelayCommand]
    private async Task CheckUpdate()
    {
        if (IsCheckingUpdate) return;
        IsCheckingUpdate = true;
        OnPropertyChanged(nameof(CheckUpdateButtonText));
        OnPropertyChanged(nameof(GitHubLabel));
        try
        {
            var info = await UpdateChecker.CheckAsync();
            ActivityLog.Write(info.HasNew ? LogLevel.Warn : LogLevel.Ok, "更新", info.Message,
                info.ReleaseUrl);

            if (info.HasNew)
            {
                UpdateHintText = "· 有新版本 " + info.LatestVersion;
                var text = new StringBuilder()
                    .AppendLine($"发现新版本 {info.LatestVersion}（当前 v{info.CurrentVersion}）")
                    .AppendLine($"发布页：{info.ReleaseUrl}");
                if (!string.IsNullOrWhiteSpace(info.DownloadUrl))
                    text.AppendLine($"下载：{info.DownloadUrl}");
                if (!string.IsNullOrWhiteSpace(info.Notes))
                    text.AppendLine().AppendLine(info.Notes);
                text.AppendLine().Append("是否打开发布页下载？");
                if (DialogService.Confirm(text.ToString(), "检查更新"))
                    ShowToast(Shell.OpenUrl(info.DownloadUrl ?? info.ReleaseUrl ?? UpdateChecker.ReleasesUrl).Message);
            }
            else if (info.Kind is UpdateKind.Unreachable or UpdateKind.RateLimited or UpdateKind.BadResponse)
            {
                UpdateHintText = "· 检查失败";
                DialogService.Warn(info.Message + "\n\n也可以直接打开发布页：" + UpdateChecker.ReleasesUrl,
                    "检查更新");
            }
            else
            {
                UpdateHintText = info.Kind == UpdateKind.NoRelease ? "· 无 Release" : "· 已是最新";
                ShowToast(info.Message);
            }
        }
        finally
        {
            IsCheckingUpdate = false;
            OnPropertyChanged(nameof(CheckUpdateButtonText));
        }
    }

    /// <summary>
    /// Startup check is opt-in and deliberately quiet: a monitoring tool that
    /// pops a dialog on every launch is noise. Only a real new version is shown.
    /// </summary>
    private async Task AutoCheckUpdateAsync()
    {
        if (!AppState.Settings.CheckUpdateOnStartup) return;
        try
        {
            var info = await UpdateChecker.CheckAsync();
            if (info.HasNew)
            {
                UpdateHintText = "· 有新版本 " + info.LatestVersion;
                ShowToast($"发现新版本 {info.LatestVersion}（当前 {VersionText}），点顶部「检查更新」查看详情");
                ActivityLog.Warn("更新", "启动检查发现新版本", info.Message);
            }
            else if (info.Kind is UpdateKind.Unreachable or UpdateKind.RateLimited or UpdateKind.BadResponse)
            {
                ActivityLog.Warn("更新", "启动检查未成功", info.Message);
            }
            else
            {
                UpdateHintText = "· 已是最新";
            }
        }
        catch (Exception ex)
        {
            ActivityLog.Warn("更新", "启动检查异常", OpResult.From(ex).Message);
        }
    }

    // ------------------------------------------------------ settings menu

    [RelayCommand]
    private void OpenSettingsPage() => CurrentPage = Settings;

    /// <summary>Two-way mirror of the setting so the menu can host a checkable item.</summary>
    public bool ShowPageHelpSetting
    {
        get => AppState.Settings.ShowPageHelp;
        set
        {
            if (value == AppState.Settings.ShowPageHelp) return;
            AppState.Settings.ShowPageHelp = value;
            AppState.Save();
            foreach (var p in Pages) p.RefreshHelpVisible();
            Settings.Reload();
            OnPropertyChanged();
        }
    }

    [RelayCommand]
    private void OpenConfigDir() => ShowToast(Shell.OpenFolder(AppState.Dir).Message);

    [RelayCommand]
    private void OpenActivityLogDir() => ShowToast(Shell.OpenFolder(ActivityLog.Dir).Message);

    [RelayCommand]
    private void ShowAbout()
    {
        var remote = _session is null ? "未连接" : _session.Label;
        DialogService.Info(
            $"WINNAS 控制台 {VersionText}\n\n" +
            $"运行时：.NET {Environment.Version}\n" +
            $"当前被控端：{remote}\n" +
            $"本机：{Environment.MachineName}\n\n" +
            $"项目仓库：{UpdateChecker.RepoUrl}\n" +
            $"联系方式：{UpdateChecker.ContactEmail}\n\n" +
            $"配置文件：{AppState.FilePath}\n" +
            $"操作日志：{ActivityLog.Dir}",
            "关于 WINNAS 控制台");
    }

    // ------------------------------------------------------ page switching

    /// <summary>Jumps to the connection page; used by the "not connected" banner.</summary>
    [RelayCommand]
    private void GoToConnections() =>
        CurrentPage = Pages.FirstOrDefault(p => p is ConnectionsViewModel) ?? Pages[^1];

    /// <summary>Ctrl+1..9 and Ctrl+0 jump straight to a page.</summary>
    [RelayCommand]
    private void GoToPage(string? indexText)
    {
        if (!int.TryParse(indexText, out var n)) return;

        // 0 means the tenth item; 1..9 map to the first nine.
        var idx = n == 0 ? 9 : n - 1;
        if (idx < 0 || idx >= Pages.Count) return;

        CurrentPage = Pages[idx];
    }

    public void TogglePageHelp()
    {
        AppState.Settings.ShowPageHelp = !AppState.Settings.ShowPageHelp;
        AppState.Save();
        foreach (var p in Pages) p.RefreshHelpVisible();
    }
}
