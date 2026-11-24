using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Winnas.Console.Infrastructure;
using Winnas.Console.Models;
using Winnas.Console.Services;

namespace Winnas.Console.ViewModels;

/// <summary>
/// Everything that has to agree between this console and the managed machine:
/// the configuration, the backup definitions, and the clock.
///
/// The page deliberately owns no connection of its own — it borrows the live
/// session through SessionProvider, so switching connections in the sidebar
/// immediately changes which NAS is being compared.
/// </summary>
public partial class SyncViewModel : PageViewModel
{
    public SyncViewModel(WinnasService service) : base(service) { }

    public override string Title => "同步";
    public override string Glyph => "⇄";
    public override string Group => "系统";

    /// <summary>Reading three remote things on every timer tick would be rude.</summary>
    public override bool AutoRefreshable => false;

    public override string HelpText =>
        "这里管的是控制台与被控端之间的一致性：把本机设置下发到 NAS 保存一份、把备份方案同步过去、" +
        "以及检查两端时钟。连接成功后会自动检查时间，偏差超过容差就把被控端校准成本机的当地时间。" +
        "密码与 AI Key 用 Windows DPAPI 按当前用户加密，换机器解不开，因此永远不参与同步。";

    // ------------------------------------------------------------ settings

    public string LocalConfigPath => AppState.FilePath;
    public string TargetConfigPath => SettingsSync.TargetPath;

    [ObservableProperty] private string localSummary = "";
    [ObservableProperty] private string localWritten = "";
    [ObservableProperty] private string targetSummary = "尚未检查";
    [ObservableProperty] private string targetWritten = "";
    [ObservableProperty] private string targetFingerprint = "";
    [ObservableProperty] private string syncStateText = "尚未检查";
    [ObservableProperty] private bool syncIsAligned;
    [ObservableProperty] private bool syncNeedsAction;
    [ObservableProperty] private bool targetExists;

    public bool SyncAutoPush
    {
        get => AppState.Settings.SyncAutoPush;
        set
        {
            if (AppState.Settings.SyncAutoPush == value) return;
            AppState.Settings.SyncAutoPush = value;
            AppState.Save();
            OnPropertyChanged();
            ActivityLog.Info("同步", value ? "已开启连接后自动下发设置" : "已关闭连接后自动下发设置");
        }
    }

    // --------------------------------------------------------------- clock

    [ObservableProperty] private string localTimeText = "—";
    [ObservableProperty] private string targetTimeText = "—";
    [ObservableProperty] private string driftText = "—";
    [ObservableProperty] private string clockDetail = "尚未检查";
    [ObservableProperty] private string clockMethod = "";
    [ObservableProperty] private bool clockAligned;
    [ObservableProperty] private bool clockDrifted;

    public List<int> ToleranceChoices { get; } = new() { 10, 30, 60, 300, 3600 };

    public int TimeToleranceSeconds
    {
        get => TimeSync.ClampTolerance(AppState.Settings.TimeToleranceSeconds);
        set
        {
            var v = TimeSync.ClampTolerance(value);
            if (AppState.Settings.TimeToleranceSeconds == v) return;
            AppState.Settings.TimeToleranceSeconds = v;
            AppState.Save();
            OnPropertyChanged();
        }
    }

    public bool TimeAutoSync
    {
        get => AppState.Settings.TimeAutoSync;
        set
        {
            if (AppState.Settings.TimeAutoSync == value) return;
            AppState.Settings.TimeAutoSync = value;
            AppState.Save();
            OnPropertyChanged();
            ActivityLog.Info("校时", value ? "已开启连接后自动校时" : "已关闭连接后自动校时");
        }
    }

    // --------------------------------------------------------- backup plans

    public ObservableCollection<BackupPlan> Plans { get; } = new();

    public bool HasPlans => Plans.Count > 0;

    [ObservableProperty] private string planCountText = "";
    [ObservableProperty] private string targetPlanText = "尚未检查";

    // -------------------------------------------------------------- loading

    public override Task LoadAsync(WinnasSession? session) => RefreshAllAsync(session);

    private async Task RefreshAllAsync(WinnasSession? session)
    {
        var s = AppState.Settings;

        // Local half never needs a connection.
        var file = new FileInfo(AppState.FilePath);
        LocalWritten = file.Exists
            ? file.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")
            : "配置文件尚未落盘";
        LocalSummary = $"{s.Connections.Count} 条连接 · {s.BackupPlans.Count} 个备份方案 · " +
                       $"{s.SnapshotPolicies.Count} 条快照策略 · 皮肤 {s.Theme}";

        Plans.Clear();
        foreach (var p in s.BackupPlans) Plans.Add(p);
        OnPropertyChanged(nameof(HasPlans));
        PlanCountText = $"本机共 {Plans.Count} 个备份方案";

        if (session is null || !session.IsRemote)
        {
            ResetRemote("当前未连接远程被控端，设置同步与校时不可用。");
            return;
        }

        // Three independent reads; one failing must not hide the other two.
        var info = await SettingsSync.InspectAsync(session);
        ApplyTargetInfo(info);

        var planInfo = await SettingsSync.InspectPlansAsync(session);
        var differs = planInfo.Count != s.BackupPlans.Count;
        TargetPlanText = planInfo.Message + (differs
            ? $"；本机有 {s.BackupPlans.Count} 个，建议下发一次。"
            : "；与本机数量一致。");

        var clock = await TimeSync.InspectAsync(session);
        ApplyClock(clock);
    }

    private void ResetRemote(string message)
    {
        TargetSummary = message;
        TargetWritten = "";
        TargetFingerprint = "";
        TargetExists = false;
        SyncStateText = message;
        SyncIsAligned = false;
        SyncNeedsAction = false;
        TargetPlanText = message;
        LocalTimeText = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        TargetTimeText = "—";
        DriftText = "—";
        ClockDetail = message;
        ClockMethod = "";
        ClockAligned = false;
        ClockDrifted = false;
    }

    private void ApplyTargetInfo(TargetSettingsInfo info)
    {
        TargetExists = info.Exists;
        TargetWritten = info.WrittenAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "—";
        TargetFingerprint = info.Fingerprint.Length > 0 ? info.Fingerprint : "—";

        if (!info.Exists)
        {
            TargetSummary = info.Message;
            SyncStateText = "尚未建立同步 — 点「下发到 NAS」建立第一份副本";
            SyncIsAligned = false;
            SyncNeedsAction = true;
            return;
        }

        var local = SettingsSync.Capture();
        var same = info.Fingerprint == SettingsSync.Fingerprint(local);
        TargetSummary = $"来自 {info.SourceMachine}（采集于 {info.CapturedAt}）：" +
                        $"{info.Connections} 条连接 · {info.BackupPlans} 个备份方案 · " +
                        $"{info.SnapshotPolicies} 条快照策略";
        SyncStateText = same
            ? "两端设置一致"
            : "两端设置不一致 — 可下发本机设置，或从 NAS 恢复";
        SyncIsAligned = same;
        SyncNeedsAction = !same;
    }

    private void ApplyClock(ClockState state)
    {
        LocalTimeText = state.LocalText;
        TargetTimeText = state.TargetText;
        ClockMethod = state.Method;
        ClockDetail = state.Detail;
        ClockAligned = state.Ok && !state.Drifted(TimeToleranceSeconds);
        ClockDrifted = state.Drifted(TimeToleranceSeconds);
        DriftText = state.Ok ? state.DriftText : "读取失败";
    }

    // ------------------------------------------------------------- commands

    [RelayCommand]
    private async Task RefreshAsync() =>
        await Run(async () => { await RefreshAllAsync(CurrentSession); });

    [RelayCommand]
    private async Task PushSettingsAsync() => await Run(async () =>
    {
        Notice = null;
        var session = RequireRemote();
        if (session is null) return;

        var r = await SettingsSync.PushAsync(session);
        Report(r, "设置下发");
        await RefreshAllAsync(session);
    });

    [RelayCommand]
    private async Task PullSettingsAsync() => await Run(async () =>
    {
        Notice = null;
        var session = RequireRemote();
        if (session is null) return;

        if (!Confirm("从 NAS 恢复设置会覆盖本机的连接、备份方案、快照策略等配置（本机已保存的密码与 API Key 会保留）。继续？"))
            return;

        var (read, snapshot) = await SettingsSync.PullAsync(session);
        if (snapshot is null) { Error = read.Message; return; }

        var applied = SettingsSync.Apply(snapshot);
        Report(applied, "设置恢复");
        await RefreshAllAsync(session);
    });

    [RelayCommand]
    private void ExportSettings()
    {
        Notice = null;
        var r = SettingsSync.ExportTo(SettingsSync.DefaultExportPath);
        Report(r, "设置导出");
    }

    [RelayCommand]
    private void ImportSettings()
    {
        Notice = null;
        var (read, snapshot) = SettingsSync.ImportFrom(SettingsSync.DefaultExportPath);
        if (snapshot is null) { Error = read.Message; return; }

        if (!Confirm($"将用 {SettingsSync.DefaultExportPath} 里的副本覆盖本机设置（密码与 API Key 保留）。继续？"))
            return;

        var applied = SettingsSync.Apply(snapshot);
        Report(applied, "设置导入");
        // The import changes AppSettings in place; rebuild the local view.
        _ = Run(async () => await RefreshAllAsync(CurrentSession));
    }

    [RelayCommand]
    private async Task PushPlansAsync() => await Run(async () =>
    {
        Notice = null;
        var session = RequireRemote();
        if (session is null) return;

        var r = await AgentDeployer.WritePlansAsync(session, AppState.Settings.BackupPlans);
        Report(r, "备份方案下发");
        await RefreshAllAsync(session);
    });

    [RelayCommand]
    private async Task CheckTimeAsync() => await Run(async () =>
    {
        Notice = null;
        var session = RequireRemote();
        if (session is null) return;

        var state = await TimeSync.InspectAsync(session);
        ApplyClock(state);
        Notice = state.Ok
            ? $"检查完成：{state.DriftText}（容差 {TimeToleranceSeconds} 秒）。"
            : null;
        if (!state.Ok) Error = state.Detail;
    });

    [RelayCommand]
    private async Task CorrectTimeAsync() => await Run(async () =>
    {
        Notice = null;
        var session = RequireRemote();
        if (session is null) return;

        var before = await TimeSync.InspectAsync(session);
        if (!before.Ok) { Error = before.Detail; return; }
        if (!before.Drifted(TimeToleranceSeconds))
        {
            Notice = $"时钟已在容差内（{before.DriftText}），未改动被控端。";
            return;
        }

        if (!Confirm($"被控端时钟{before.DriftText}。将把它设为本机时间 {before.LocalText}。" +
                     "改系统时间会影响被控端上的计划任务与日志时间戳，继续？"))
            return;

        var r = await TimeSync.CorrectAsync(session, session.Profile, TimeToleranceSeconds);
        Report(r, "时间校准");
        await RefreshAllAsync(session);
    });

    // -------------------------------------------------------------- helpers

    private WinnasSession? RequireRemote()
    {
        var session = CurrentSession;
        if (session is null || !session.IsRemote)
        {
            Error = "需要先连接远程被控端；本机模式下没有可同步或校准的对象。";
            return null;
        }
        return session;
    }

    private void Report(OpResult result, string what)
    {
        if (result.Ok) Notice = result.Message;
        else Error = result.Message;
        ActivityLog.Write(result.Ok ? LogLevel.Ok : LogLevel.Error, "同步", what, result.Message);
    }

    private static bool Confirm(string text) =>
        MessageBox.Show(text, "WINNAS 同步", MessageBoxButton.YesNo,
            MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
}
