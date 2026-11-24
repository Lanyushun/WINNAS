using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Winnas.Console.Infrastructure;
using Winnas.Console.Services;

namespace Winnas.Console.ViewModels;

/// <summary>
/// Activity journal + offline help in one page: when something fails, the
/// explanation and the evidence belong on the same screen.
/// </summary>
public partial class HelpViewModel : PageViewModel
{
    private const int DisplayLimit = 500;

    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private bool _subscribed;

    public ObservableCollection<LogEntry> Entries { get; } = new();

    public List<HelpGroup> Groups { get; } = HelpTopics.All;

    [ObservableProperty] private HelpGroup? selectedGroup;

    [ObservableProperty] private LogEntry? selectedEntry;

    [ObservableProperty] private int levelFilter = -1;

    [ObservableProperty] private string searchText = "";

    [ObservableProperty] private bool autoFollow = true;

    [ObservableProperty] private int keepDays = AppState.Settings.LogKeepDays;

    public HelpViewModel(WinnasService service) : base(service)
    {
        SelectedGroup = Groups.FirstOrDefault();
        ActivityLog.EntryAdded += OnEntryAdded;
        _subscribed = true;
    }

    public override string Title => "日志与帮助";
    public override string Glyph => "❔";
    public override string Group => "系统";

    public override string HelpText =>
        "上半区是本程序自己记的操作日志：连接、建删共享、快照、上传下载、备份触发、AI 请求与所有失败。" +
        "下半区是离线帮助，涵盖远程配置、错误码含义与快捷键。";

    // ------------------------------------------------------------- computed

    public List<LogEntry> VisibleEntries
    {
        get
        {
            var q = SearchText.Trim();
            return Entries.Where(e =>
                (LevelFilter < 0 || (int)e.Level == LevelFilter) &&
                (q.Length == 0 ||
                 e.Message.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                 e.Category.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                 (e.Detail?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)))
                .Take(DisplayLimit)
                .ToList();
        }
    }

    public int TotalCount => Entries.Count;
    public int ErrorCount => Entries.Count(e => e.Level == LogLevel.Error);
    public int WarnCount => Entries.Count(e => e.Level == LogLevel.Warn);

    public bool HasSearch => SearchText.Trim().Length > 0;

    public string[] LevelOptions { get; } = { "全部", "信息", "成功", "警告", "错误" };

    /// <summary>String bridge so the level ComboBox can bind without a converter.</summary>
    public string LevelText
    {
        get => LevelFilter < 0 ? LevelOptions[0] : LevelOptions[LevelFilter + 1];
        set
        {
            var idx = Array.IndexOf(LevelOptions, value);
            LevelFilter = idx <= 0 ? -1 : idx - 1;
            OnPropertyChanged(nameof(LevelFilter));
            OnPropertyChanged(nameof(VisibleEntries));
        }
    }

    public string ConfigPath => AppState.FilePath;
    public string LogDir => ActivityLog.Dir;
    public string VersionText => "v" + HelpTopics.Version;
    public string RuntimeText => Environment.Version.ToString();

    public string SummaryText => $"共 {TotalCount} 条 · {ErrorCount} 错误 · {WarnCount} 警告";

    public override async Task LoadAsync(WinnasSession? session) => await Run(async () =>
    {
        Entries.Clear();
        var history = await Task.Run(() => ActivityLog.LoadHistory(days: 3));
        foreach (var e in history) Entries.Add(e);
        RefreshCounts();
    });

    // ------------------------------------------------------------- commands

    [RelayCommand]
    private async Task Reload() => await LoadAsync(CurrentSession);

    [RelayCommand]
    private void ClearSearch() => SearchText = "";

    [RelayCommand]
    private void SetLevel(string? value)
    {
        LevelFilter = int.TryParse(value, out var n) ? n : -1;
        OnPropertyChanged(nameof(VisibleEntries));
    }

    [RelayCommand]
    private void ClearLogs()
    {
        if (!DialogService.Confirm(
                "清空操作日志？\n\n这会删除日志目录里的全部历史文件，只保留本次会话内存中的记录。\n此操作不可撤销。",
                "清空日志"))
            return;

        // Our own append handle would make every delete fail silently.
        ActivityLog.ReleaseFile();

        try
        {
            if (Directory.Exists(ActivityLog.Dir))
                foreach (var f in new DirectoryInfo(ActivityLog.Dir).EnumerateFiles("activity-*.log"))
                {
                    try { f.Delete(); } catch { /* in use */ }
                }
        }
        catch { /* ignore */ }

        Entries.Clear();
        ActivityLog.ClearMemory();
        RefreshCounts();
        Notice = "日志已清空";
        ActivityLog.Info("日志", "用户清空了操作日志");
    }

    [RelayCommand]
    private void PruneOld()
    {
        var removed = ActivityLog.Prune(KeepDays);
        AppState.Settings.LogKeepDays = KeepDays;
        AppState.Save();

        Notice = removed == 0 ? $"没有超过 {KeepDays} 天的日志文件" : $"已删除 {removed} 个旧日志文件";
        ActivityLog.Info("日志", Notice, $"保留 {KeepDays} 天");
    }

    [RelayCommand]
    private void ExportLogs()
    {
        var rows = VisibleEntries;
        if (rows.Count == 0) { Error = "没有可导出的日志"; return; }

        var result = Exporter.SaveText($"winnas-log-{DateTime.Now:yyyyMMdd-HHmm}.txt",
            ActivityLog.ToText(rows));

        if (!result.Ok) Error = result.Message;
        else Notice = result.Message;
    }

    [RelayCommand]
    private void OpenLogDir() => OpenPath(ActivityLog.Dir, true);

    [RelayCommand]
    private void OpenConfigDir() => OpenPath(AppState.Dir, false);

    [RelayCommand]
    private void CopyPaths()
    {
        var text = $"配置：{ConfigPath}\n日志：{LogDir}";
        try
        {
            System.Windows.Clipboard.SetText(text);
            Notice = "路径已复制到剪贴板";
        }
        catch { Error = "无法访问剪贴板"; }
    }

    private static void OpenPath(string dir, bool create)
    {
        try
        {
            if (create) Directory.CreateDirectory(dir);
            if (!Directory.Exists(dir)) return;
            Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
        }
        catch { /* ignore */ }
    }

    // -------------------------------------------------------------- plumbing

    private void OnEntryAdded(LogEntry entry)
    {
        // Log calls come from worker threads; the collection is bound to the UI.
        if (_ui.CheckAccess()) Append(entry);
        else _ui.BeginInvoke(() => Append(entry));
    }

    private void Append(LogEntry entry)
    {
        Entries.Insert(0, entry);
        while (Entries.Count > DisplayLimit) Entries.RemoveAt(Entries.Count - 1);

        if (AutoFollow) SelectedEntry = entry;
        RefreshCounts();
    }

    private void RefreshCounts()
    {
        OnPropertyChanged(nameof(VisibleEntries));
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(ErrorCount));
        OnPropertyChanged(nameof(WarnCount));
        OnPropertyChanged(nameof(SummaryText));
    }

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(VisibleEntries));
        OnPropertyChanged(nameof(HasSearch));
    }

    partial void OnLevelFilterChanged(int value) => OnPropertyChanged(nameof(VisibleEntries));

    public void Detach()
    {
        if (!_subscribed) return;
        ActivityLog.EntryAdded -= OnEntryAdded;
        _subscribed = false;
    }
}
