using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Winnas.Console.Infrastructure;
using Winnas.Console.Models;
using Winnas.Console.Services;

namespace Winnas.Console.ViewModels;

public partial class BackupViewModel : PageViewModel
{
    private const int MaxOutputLines = 400;

    private readonly Action _onSettingsSaved;
    private CancellationTokenSource? _cts;

    public BackupViewModel(WinnasService service, Action onSettingsSaved) : base(service)
    {
        _onSettingsSaved = onSettingsSaved;
        ReloadPlans();
        Selected = Plans.FirstOrDefault();
        History = new ObservableCollection<BackupRun>(BackupRunStore.Load().Take(120));
    }

    // ------------------------------------------------------------- page

    public override string Title => "备份";
    public override string Glyph => "⟳";
    public override string Group => "服务";

    public override string HelpText =>
        "这里的「备份计划」是在被管理的机器上执行的：源和目标都填那台机器的本地路径（例如 D:\\NAS → E:\\NAS-Mirror）。" +
        "先「检查」看机器当前状态是否允许执行，再「预演」确认会复制/删除什么，最后才「执行」。镜像模式会把源里的删除同步到目标，务必配合「先建快照」。";

    // --------------------------------------------------- legacy task view

    [ObservableProperty] private BackupInfo? info;
    [ObservableProperty] private string? selectedLog;
    [ObservableProperty] private string logText = "";
    [ObservableProperty] private bool measuring;
    [ObservableProperty] private string settingsTask = AppState.Settings.BackupTask;
    [ObservableProperty] private string settingsSource = AppState.Settings.BackupSource;
    [ObservableProperty] private string settingsLogDir = AppState.Settings.BackupLogDir;
    [ObservableProperty] private int settingsRefresh = AppState.Settings.RefreshSeconds;
    [ObservableProperty] private bool settingsVisible;

    public bool HasLog => LogText.Length > 0;

    // ------------------------------------------------------------- plans

    [ObservableProperty] private ObservableCollection<BackupPlan> plans = new();
    [ObservableProperty] private BackupPlan? selected;
    [ObservableProperty] private ObservableCollection<PreflightCheck> checks = new();
    [ObservableProperty] private ObservableCollection<string> output = new();
    [ObservableProperty] private ObservableCollection<BackupRun> history = new();
    [ObservableProperty] private bool onlyCurrent;
    [ObservableProperty] private bool running;
    [ObservableProperty] private bool editorVisible = true;
    [ObservableProperty] private string commandPreview = "";

    public bool HasPlan => Selected is not null;
    public bool HasChecks => Checks.Count > 0;
    public bool HasBlocking => Checks.Any(c => !c.Ok && c.Blocking);
    public bool HasOutput => Output.Count > 0;
    public bool HasHistory => History.Count > 0;
    public bool IsVersioned => Selected?.Mode == 2;
    public bool IsManual => Selected?.Frequency == 3;

    /// <summary>Raised after a line is appended so the view can scroll to the end.</summary>
    public event Action? OutputAppended;

    public override async Task LoadAsync(WinnasSession? session) => await Run(async () =>
    {
        if (session is null) { Info = null; return; }
        Info = await Service.GetBackupAsync(session);
    });

    partial void OnSelectedChanged(BackupPlan? value)
    {
        OnPropertyChanged(nameof(HasPlan));
        OnPropertyChanged(nameof(IsVersioned));
        OnPropertyChanged(nameof(IsManual));
        Checks.Clear();
        OnPropertyChanged(nameof(HasChecks));
        UpdatePreview();
        RefreshHistory();
    }

    partial void OnChecksChanged(ObservableCollection<PreflightCheck> value)
    {
        OnPropertyChanged(nameof(HasChecks));
        OnPropertyChanged(nameof(HasBlocking));
    }

    partial void OnOnlyCurrentChanged(bool value) => RefreshHistory();

    private void ReloadPlans()
    {
        Plans = new ObservableCollection<BackupPlan>(AppState.Settings.BackupPlans);
    }

    /// <summary>
    /// Refreshes the rows in place. Rebuilding the collection would make the
    /// DataGrid write null back into Selected, which hides the editor and
    /// throws away the check results the user is reading.
    /// </summary>
    private void RefreshPlans()
    {
        System.Windows.Data.CollectionViewSource.GetDefaultView(Plans).Refresh();
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(IsVersioned));
        OnPropertyChanged(nameof(IsManual));
    }

    private void RefreshHistory()
    {
        var runs = OnlyCurrent ? BackupRunStore.For(Selected?.Id) : BackupRunStore.Load();
        History = new ObservableCollection<BackupRun>(runs.Take(120));
        OnPropertyChanged(nameof(HasHistory));
    }

    private void UpdatePreview()
    {
        CommandPreview = Selected is null ? "" : PreviewOf(Selected);
        OnPropertyChanged(nameof(CommandPreview));
    }

    private static string PreviewOf(BackupPlan plan)
    {
        var now = DateTime.Now;
        var target = BackupPlanner.ResolveTarget(plan, now);
        return "robocopy " + BackupPlanner.BuildArgs(plan, plan.Source, target,
            BackupPlanner.LocalLogPath(plan, now));
    }

    /// <summary>Called from a background thread, so it always marshals.</summary>
    private void Append(string line)
    {
        void Add()
        {
            Output.Add(line);
            while (Output.Count > MaxOutputLines) Output.RemoveAt(0);
            OnPropertyChanged(nameof(HasOutput));
            OutputAppended?.Invoke();
        }

        var app = Application.Current;
        if (app is null) { Add(); return; }
        app.Dispatcher.BeginInvoke(Add);
    }

    // --------------------------------------------------------- plan CRUD

    [RelayCommand]
    private void NewPlan()
    {
        var plan = new BackupPlan
        {
            Name = $"备份计划 {Plans.Count + 1}",
            Source = AppState.Settings.BackupSource,
        };
        AppState.Settings.BackupPlans.Add(plan);
        Plans.Add(plan);
        AppState.Save();
        Selected = plan;
        EditorVisible = true;
        Notice = "已新建计划，填好源与目标后先做一次「预演」";
        ActivityLog.Info("备份", "新建备份计划", plan.Name);
    }

    [RelayCommand]
    private void DuplicatePlan()
    {
        if (Selected is null) return;
        var copy = Selected.Clone();
        copy.Id = Guid.NewGuid().ToString("N")[..8];
        copy.Name = Selected.Name + " 副本";
        copy.Scheduled = false;
        copy.LastRun = null;
        copy.LastSuccess = null;
        copy.LastError = null;

        AppState.Settings.BackupPlans.Add(copy);
        Plans.Add(copy);
        AppState.Save();
        Selected = copy;
    }

    [RelayCommand]
    private void DeletePlan()
    {
        if (Selected is null) return;
        if (!DialogService.Confirm($"删除计划「{Selected.Name}」？\n历史记录会保留，定时任务需要单独注销。", "删除备份计划"))
            return;

        try
        {
            var session = CurrentSession;
            if (session is not null && Selected.Scheduled)
                new BackupPlanner(session, Service).Unregister(Selected);
        }
        catch { /* removing the definition matters more than the task */ }

        var removed = Selected;
        AppState.Settings.BackupPlans.RemoveAll(p => p.Id == removed.Id);
        Plans.Remove(removed);
        AppState.Save();

        Selected = Plans.FirstOrDefault();
        ActivityLog.Warn("备份", "删除备份计划", removed.Name);
    }

    [RelayCommand]
    private void SavePlan()
    {
        if (Selected is null) return;

        if (string.IsNullOrWhiteSpace(Selected.Source) || string.IsNullOrWhiteSpace(Selected.Target))
        {
            Error = "源目录与目标目录都不能为空";
            return;
        }

        AppState.Save();
        UpdatePreview();
        RefreshPlans();
        Notice = $"计划「{Selected.Name}」已保存";
        ActivityLog.Info("备份", "保存备份计划", Selected.Summary);
    }

    [RelayCommand]
    private void ToggleEditor() => EditorVisible = !EditorVisible;

    // ---------------------------------------------------------- checks

    [RelayCommand]
    private async Task CheckAsync()
    {
        var session = CurrentSession;
        if (session is null || Selected is null) return;

        await Run(async () =>
        {
            var planner = new BackupPlanner(session, Service);
            var result = await planner.PreflightAsync(Selected);
            Checks = new ObservableCollection<PreflightCheck>(result);
            var blocking = result.Count(c => !c.Ok && c.Blocking);
            Notice = blocking == 0
                ? "前置检查通过，可以执行"
                : $"有 {blocking} 项阻止执行，请看检查结果";
            ActivityLog.Info("备份", "前置检查", $"{Selected.Name}：{blocking} 项阻止");
        });
    }

    // ------------------------------------------------------------- runs

    [RelayCommand]
    private async Task DryRunAsync() => await RunPlanAsync(dryRun: true);

    [RelayCommand]
    private async Task ExecuteAsync() => await RunPlanAsync(dryRun: false);

    private async Task RunPlanAsync(bool dryRun)
    {
        var session = CurrentSession;
        var plan = Selected;
        if (session is null || plan is null) return;

        Running = true;
        Error = null;
        Notice = null;
        Output.Clear();
        _cts = new CancellationTokenSource();

        try
        {
            var planner = new BackupPlanner(session, Service);

            var pre = await planner.PreflightAsync(plan);
            Checks = new ObservableCollection<PreflightCheck>(pre);

            var blocking = pre.Where(c => !c.Ok && c.Blocking).ToList();
            if (blocking.Count > 0)
            {
                Append("[阻止] " + string.Join("；", blocking.Select(b => $"{b.Name}（{b.Detail}）")));
                Error = "前置检查未通过，没有执行。修正后在「检查」里复核。";
                ActivityLog.Warn("备份", "备份被前置检查拦下", $"{plan.Name}：{string.Join("；", blocking.Select(b => b.Name))}");
                return;
            }

            Append($"[{DateTime.Now:HH:mm:ss}] 开始{(dryRun ? "预演" : "执行")}：{plan.Name}");

            var run = dryRun
                ? await planner.DryRunAsync(plan, Append, _cts.Token)
                : await planner.RunAsync(plan, Append, _cts.Token);

            if (run.Ok)
            {
                Notice = $"{(dryRun ? "预演" : "备份")}完成：{run.Files} 个文件 / {run.SizeText}，耗时 {run.DurationText}";
                ActivityLog.Ok("备份", dryRun ? "预演完成" : "备份完成", $"{plan.Name}：{run.Message}");
            }
            else
            {
                Error = run.Message;
                ActivityLog.Error("备份", dryRun ? "预演失败" : "备份失败", $"{plan.Name}：{run.Message}");
            }

            RefreshPlans();
            RefreshHistory();
            await LoadAsync(session);
        }
        catch (Exception ex)
        {
            Error = OpResult.From(ex).Message;
        }
        finally
        {
            Running = false;
            _cts = null;
        }
    }

    [RelayCommand]
    private void CancelRun()
    {
        _cts?.Cancel();
        Append("[取消] 已请求停止（目标机上的 robocopy 可能仍在收尾）");
    }

    [RelayCommand]
    private void ClearOutput()
    {
        Output.Clear();
        OnPropertyChanged(nameof(HasOutput));
    }

    // ------------------------------------------------------- scheduling

    [RelayCommand]
    private async Task Register()
    {
        var session = CurrentSession;
        if (session is null || Selected is null) return;

        try
        {
            IsBusy = true;
            var result = await new BackupPlanner(session, Service).RegisterAsync(Selected);
            if (result.Ok) { Notice = result.Message; ActivityLog.Ok("备份", "注册定时任务", result.Message); }
            else { Error = result.Message; ActivityLog.Error("备份", "注册定时任务失败", result.Message); }
            RefreshPlans();
        }
        catch (Exception ex) { Error = OpResult.From(ex).Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void Unregister()
    {
        var session = CurrentSession;
        if (session is null || Selected is null) return;

        if (!DialogService.Confirm($"注销「{Selected.Name}」的定时任务？计划本身会保留。", "注销定时任务")) return;

        try
        {
            var result = new BackupPlanner(session, Service).Unregister(Selected);
            if (result.Ok) { Notice = result.Message; ActivityLog.Info("备份", "注销定时任务", result.Message); }
            else Error = result.Message;
            RefreshPlans();
        }
        catch (Exception ex) { Error = OpResult.From(ex).Message; }
    }

    [RelayCommand]
    private void ExportScript()
    {
        var session = CurrentSession;
        if (session is null || Selected is null) return;

        var script = new BackupPlanner(session, Service).BuildScript(Selected);
        var safe = string.Concat(Selected.Name.Split(Path.GetInvalidFileNameChars()));
        if (DialogService.SaveText($"{(string.IsNullOrWhiteSpace(safe) ? "backup" : safe)}.bat", script,
                "批处理|*.bat|文本文件|*.txt"))
        {
            Notice = "脚本已导出，可在目标机上以管理员身份运行或注册为计划任务";
            ActivityLog.Info("备份", "导出备份脚本", Selected.Name);
        }
    }

    // ---------------------------------------------------- legacy commands

    [RelayCommand]
    private void ToggleSettings() => SettingsVisible = !SettingsVisible;

    [RelayCommand]
    private void SaveSettings()
    {
        AppState.Settings.BackupTask = SettingsTask.Trim();
        AppState.Settings.BackupSource = SettingsSource.Trim();
        AppState.Settings.BackupLogDir = SettingsLogDir.Trim();
        var refresh = SettingsRefresh is < 5 or > 600 ? 10 : SettingsRefresh;
        AppState.Settings.RefreshSeconds = refresh;

        // Write the clamped value back so the box cannot keep showing a number
        // the app silently ignored.
        SettingsRefresh = refresh;

        AppState.Save();
        _onSettingsSaved();

        SettingsVisible = false;
        Notice = "设置已保存";
        ActivityLog.Info("设置", "备份设置已保存",
            $"任务 {SettingsTask} · 源 {SettingsSource} · 刷新 {refresh}s");
    }

    [RelayCommand]
    private async Task RunTask()
    {
        var session = CurrentSession;
        if (session is null) return;

        var result = await Service.RunBackupTaskAsync(session);
        if (!result.Ok)
        {
            Error = result.Message;
            ActivityLog.Error("备份", "触发备份任务失败", result.Message);
            return;
        }

        ActivityLog.Ok("备份", result.Message);
        Notice = result.Message;
        await LoadAsync(session);
    }

    /// <summary>
    /// Walking the source tree over SMB can take minutes on a full NAS, so it is
    /// always user-initiated — never part of an auto refresh.
    /// </summary>
    [RelayCommand]
    private async Task MeasureSource()
    {
        var session = CurrentSession;
        if (session is null) return;

        Measuring = true;
        try
        {
            await Service.MeasureSourceAsync(session);
            await LoadAsync(session);
        }
        finally { Measuring = false; }
    }

    [RelayCommand]
    private async Task ViewLog(LogFileInfo? log)
    {
        var session = CurrentSession;
        if (session is null || log is null) return;

        SelectedLog = log.Name;
        var lines = await Service.ReadLogTailAsync(session, log.Name);
        LogText = lines.Length == 0 ? "(日志文件为空或无法读取)" : string.Join(Environment.NewLine, lines);
    }

    [RelayCommand]
    private void CloseLog()
    {
        SelectedLog = null;
        LogText = "";
    }

    partial void OnLogTextChanged(string value) => OnPropertyChanged(nameof(HasLog));
}
