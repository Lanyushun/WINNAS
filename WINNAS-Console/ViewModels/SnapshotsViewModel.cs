using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Winnas.Console.Infrastructure;
using Winnas.Console.Models;
using Winnas.Console.Services;

namespace Winnas.Console.ViewModels;

public partial class SnapshotsViewModel : PageViewModel
{
    [ObservableProperty] private List<SnapshotInfo> snapshots = new();

    [ObservableProperty] private List<string> volumes = new();

    [ObservableProperty] private string? targetVolume;

    // ------------------------------------------------------------- policies

    [ObservableProperty] private List<SnapshotPolicy> policies = new();

    [ObservableProperty] private SnapshotPolicy? selectedPolicy;

    [ObservableProperty] private bool policyFormVisible;

    [ObservableProperty] private string policyVolume = "D:\\";

    [ObservableProperty] private int policyFrequency;

    [ObservableProperty] private int policyHour = 3;

    [ObservableProperty] private int policyMinute;

    [ObservableProperty] private int policyDayOfWeek = 1;

    [ObservableProperty] private int policyKeep = 5;

    [ObservableProperty] private bool taskRegistered;

    public SnapshotsViewModel(WinnasService service) : base(service) { }

    public override string Title => "快照";
    public override string Glyph => "◐";
    public override string Group => "服务";

    public override string HelpText =>
        "定时策略的执行方式：注册一个每小时触发的系统计划任务跑本程序的 --snapshot 参数，由程序自己判断哪条策略到期。" +
        "这样不同频率的策略共用一个触发器，也不会因休眠错过时间点。每卷上限 64 个快照，超了会创建失败。";

    public bool ShowWeekly => PolicyFrequency == 1;

    public string TaskStateText => TaskRegistered
        ? "已注册系统计划任务，每小时检查一次"
        : "尚未注册计划任务，策略不会自动执行";

    public override async Task LoadAsync(WinnasSession? session) => await Run(async () =>
    {
        Policies = AppState.Settings.SnapshotPolicies;
        TaskRegistered = SnapshotPolicyService.IsTaskRegistered(CurrentSession);

        if (session is null)
        {
            Snapshots = new List<SnapshotInfo>();
            Volumes = new List<string>();
            return;
        }

        Snapshots = await Service.GetSnapshotsAsync(session);
        var vols = await Service.GetVolumeLettersAsync(session);
        Volumes = vols;

        if (TargetVolume is null && vols.Count > 0)
        {
            var preferred = vols.FirstOrDefault(v => v.StartsWith("D:", StringComparison.OrdinalIgnoreCase)) ?? vols[0];
            TargetVolume = preferred;
        }

        if (PolicyVolume.Length == 0 || !vols.Contains(PolicyVolume, StringComparer.OrdinalIgnoreCase))
        {
            if (vols.Count > 0)
                PolicyVolume = vols.FirstOrDefault(v => v.StartsWith("D:", StringComparison.OrdinalIgnoreCase)) ?? vols[0];
        }
    });

    // --------------------------------------------------------- manual ops

    [RelayCommand]
    private async Task CreateSnapshot()
    {
        var session = CurrentSession;
        if (session is null) return;
        if (string.IsNullOrWhiteSpace(TargetVolume)) { Error = "请先选择卷"; return; }

        var result = await Service.CreateSnapshotAsync(session, TargetVolume);
        if (!result.Ok)
        {
            Error = result.Message;
            ActivityLog.Error("快照", $"在 {TargetVolume} 创建卷影副本失败", result.Message);
            return;
        }

        ActivityLog.Ok("快照", result.Message);
        Notice = result.Message;
        await LoadAsync(session);
    }

    [RelayCommand]
    private async Task DeleteSnapshot(SnapshotInfo? snapshot)
    {
        var session = CurrentSession;
        if (session is null || snapshot is null) return;

        if (!DialogService.Confirm(
                $"删除该卷影副本？\n\n卷：{snapshot.VolumeName}\n创建时间：{Format.Date(snapshot.InstallDate)}\n\n此操作不可撤销。",
                "删除卷影副本"))
            return;

        var result = await Service.DeleteSnapshotAsync(session, snapshot.Id);
        if (!result.Ok)
        {
            Error = result.Message;
            ActivityLog.Error("快照", "删除卷影副本失败", result.Message);
            return;
        }

        ActivityLog.Warn("快照", result.Message, snapshot.VolumeName);
        Notice = result.Message;
        await LoadAsync(session);
    }

    // ------------------------------------------------------------ policies

    [RelayCommand]
    private void OpenPolicyForm()
    {
        PolicyFormVisible = true;
        if (Volumes.Count > 0 && !Volumes.Contains(PolicyVolume, StringComparer.OrdinalIgnoreCase))
            PolicyVolume = Volumes[0];
    }

    [RelayCommand]
    private void CancelPolicyForm() => PolicyFormVisible = false;

    partial void OnPolicyFrequencyChanged(int value) => OnPropertyChanged(nameof(ShowWeekly));

    [RelayCommand]
    private async Task AddPolicy()
    {
        if (string.IsNullOrWhiteSpace(PolicyVolume)) { Error = "请选择卷"; return; }

        var policy = new SnapshotPolicy
        {
            Volume = PolicyVolume,
            Enabled = true,
            Frequency = PolicyFrequency,
            Hour = Math.Clamp(PolicyHour, 0, 23),
            Minute = Math.Clamp(PolicyMinute, 0, 59),
            DayOfWeek = Math.Clamp(PolicyDayOfWeek, 0, 6),
            Keep = Math.Clamp(PolicyKeep, 1, 64),
        };

        AppState.Settings.SnapshotPolicies.Add(policy);
        AppState.Save();

        PolicyFormVisible = false;
        Notice = $"已添加策略：{policy.Summary}";
        ActivityLog.Ok("快照", "已添加定时快照策略", policy.Summary);
        await LoadAsync(CurrentSession);
    }

    [RelayCommand]
    private async Task RemovePolicy(SnapshotPolicy? policy)
    {
        if (policy is null) return;
        if (!DialogService.Confirm($"删除这条定时策略？\n\n{policy.Summary}\n\n只是不再自动创建，已有快照不受影响。",
                "删除策略"))
            return;

        AppState.Settings.SnapshotPolicies.Remove(policy);
        AppState.Save();

        Notice = "已删除策略";
        ActivityLog.Info("快照", "已删除定时快照策略", policy.Summary);
        await LoadAsync(CurrentSession);
    }

    /// <summary>Runs every enabled policy right now, ignoring the schedule.</summary>
    [RelayCommand]
    private async Task RunPoliciesNow()
    {
        var session = CurrentSession;
        if (session is null) return;
        if (AppState.Settings.SnapshotPolicies.Count == 0) { Error = "还没有配置任何策略"; return; }

        var log = await SnapshotPolicyService.RunDueAsync(session, Service, force: true);
        Notice = log.Count == 0 ? "没有可执行的策略" : string.Join("；", log);
        ActivityLog.Info("快照", "手动执行全部策略", Notice);
        await LoadAsync(session);
    }

    [RelayCommand]
    private async Task RegisterPolicyTask()
    {
        var session = CurrentSession;
        if (session is null) { Error = "先连接被控端。"; return; }

        IsBusy = true;
        OpResult result;
        try { result = await SnapshotPolicyService.RegisterTaskAsync(session); }
        finally { IsBusy = false; }

        if (!result.Ok)
        {
            Error = result.Message;
            ActivityLog.Error("快照", "注册计划任务失败", result.Message);
            return;
        }

        ActivityLog.Ok("快照", result.Message);
        Notice = result.Message;
        await LoadAsync(CurrentSession);
    }

    [RelayCommand]
    private async Task UnregisterPolicyTask()
    {
        if (!DialogService.Confirm("删除系统里的自动执行计划任务？\n\n策略本身会保留，但不再自动运行。",
                "撤销计划任务"))
            return;

        var session = CurrentSession;
        if (session is null) { Error = "先连接被控端。"; return; }

        var result = SnapshotPolicyService.UnregisterTask(session);
        if (!result.Ok) { Error = result.Message; return; }

        Notice = result.Message;
        await LoadAsync(CurrentSession);
    }
}
