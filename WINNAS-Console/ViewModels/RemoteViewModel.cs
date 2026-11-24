using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Winnas.Console.Infrastructure;
using Winnas.Console.Models;
using Winnas.Console.Services;

namespace Winnas.Console.ViewModels;

/// <summary>
/// Remote control page.
///
/// The console runs on the desktop; everything listed here happens on the
/// managed machine. That distinction is shown, not assumed: the page leads
/// with the target's name, and every confirmation dialog names it again so a
/// "reboot" can never be mistaken for rebooting the PC you are sitting at.
/// </summary>
public partial class RemoteViewModel : PageViewModel
{
    private readonly List<RemoteProcess> _allProcesses = new();
    private readonly List<RemoteService> _allServices = new();

    public RemoteViewModel(WinnasService service) : base(service) { }

    public override string Title => "远控";
    public override string Glyph => "⏻";
    public override string Group => "控制";

    /// <summary>
    /// Auto-refresh would re-pull the whole process list every few seconds and
    /// fight the user's selection. Control actions are explicit, not polls.
    /// </summary>
    public override bool AutoRefreshable => false;

    public override string HelpText =>
        "这里的所有操作都作用在「被控端」（你连接的 NAS）上，不是你正在用的这台电脑。" +
        "关机/重启/结束进程/服务启停都需要被控端的管理员权限；远程命令的输出通过在被控端写临时文件再读回，所以命令里不要带交互式的确认。";

    // ------------------------------------------------------------- target

    public string TargetLabel => CurrentSession is null ? "（未连接）" : CurrentSession.Label;

    public bool IsRemoteTarget => CurrentSession?.IsRemote ?? false;

    public string TargetHint => CurrentSession is null
        ? "尚未连接，先在「连接」页连到被控端。"
        : IsRemoteTarget
            ? $"以下操作全部发生在被控端 {CurrentSession!.Profile.Target}（{CurrentSession.Profile.TransportText}）。"
            : "当前连接的是本机，操作会直接作用在这台电脑上。";

    // ---------------------------------------------------------- processes

    public ObservableCollection<RemoteProcess> Processes { get; } = new();

    [ObservableProperty] private RemoteProcess? selectedProcess;

    [ObservableProperty] private string procFilter = "";

    [ObservableProperty] private string procSummary = "";

    // ----------------------------------------------------------- services

    public ObservableCollection<RemoteService> Services { get; } = new();

    [ObservableProperty] private RemoteService? selectedService;

    [ObservableProperty] private string svcFilter = "";

    [ObservableProperty] private string svcSummary = "";

    // ------------------------------------------------------------ command

    [ObservableProperty] private string commandText = "";

    [ObservableProperty] private string commandOutput = "";

    [ObservableProperty] private bool commandRunning;

    [ObservableProperty] private int timeoutIndex = 2;

    [ObservableProperty] private string commandStatus = "";

    public List<int> TimeoutChoices { get; } = new() { 15, 30, 60, 120, 300 };

    public List<string> TimeoutLabels { get; } = new() { "15 秒", "30 秒", "1 分钟", "2 分钟", "5 分钟" };

    public int TimeoutSeconds =>
        TimeoutIndex >= 0 && TimeoutIndex < TimeoutChoices.Count ? TimeoutChoices[TimeoutIndex] : 60;

    /// <summary>
    /// Ready-made diagnostics. Each one is a command that answers a question
    /// people actually have about a NAS: who is connected, what is shared, why
    /// is the disk full, what broke last night.
    /// </summary>
    public List<CommandPreset> Presets { get; } = new()
    {
        new() { Name = "SMB 会话", Command = "net sessions",
                Note = "谁正连着这台 NAS 的共享" },
        new() { Name = "共享列表", Command = "net share",
                Note = "所有共享与其实际路径" },
        new() { Name = "已登录用户", Command = "query user",
                Note = "被控端当前有没有人登录" },
        new() { Name = "卷空间", Command = "powershell -NoProfile -Command \"Get-CimInstance Win32_LogicalDisk | Select-Object DeviceID,@{n='UsedGB';e={[math]::Round(($_.Size-$_.FreeSpace)/1GB,1)}},@{n='FreeGB';e={[math]::Round($_.FreeSpace/1GB,1)}} | Format-Table -Auto\"",
                Note = "每个卷的已用/剩余" },
        new() { Name = "卷影副本", Command = "vssadmin list shadows",
                Note = "可回滚的快照有哪些" },
        new() { Name = "最近错误日志",
                Command = "powershell -NoProfile -Command \"Get-EventLog -LogName System -Newest 15 -EntryType Error,Warning | Format-Table TimeGenerated,Source,Message -Wrap -Auto\"",
                Note = "近 15 条系统错误/警告" },
        new() { Name = "计划任务", Command = "schtasks /query /fo TABLE",
                Note = "备份与快照任务的注册情况" },
        new() { Name = "端口与连接", Command = "netstat -ano",
                Note = "谁在用什么端口连被控端" },
        new() { Name = "上次唤醒源", Command = "powercfg /lastwake",
                Note = "机器上次是怎么被唤醒的" },
        new() { Name = "网络连通", Command = "ping -n 4 223.5.5.5",
                Note = "被控端能不能出网" },
    };

    // -------------------------------------------------------------- power

    [ObservableProperty] private int delayIndex;

    [ObservableProperty] private bool forcePower = true;

    [ObservableProperty] private string powerReason = "";

    public List<int> DelayChoices { get; } = new() { 0, 30, 60, 180, 600 };

    public List<string> DelayLabels { get; } = new() { "立即执行", "30 秒后", "1 分钟后", "3 分钟后", "10 分钟后" };

    public int DelaySeconds =>
        DelayIndex >= 0 && DelayIndex < DelayChoices.Count ? DelayChoices[DelayIndex] : 0;

    public string DelayText => DelaySeconds == 0 ? "立即执行" : $"{DelaySeconds} 秒后执行";

    // ---------------------------------------------------------- messaging

    [ObservableProperty] private string notifyText = "";

    [ObservableProperty] private string wakeMac = "";

    // ---------------------------------------------------------------- load

    public override async Task LoadAsync(WinnasSession? session) => await Run(async () =>
    {
        OnPropertyChanged(nameof(TargetLabel));
        OnPropertyChanged(nameof(IsRemoteTarget));
        OnPropertyChanged(nameof(TargetHint));

        // Wake-on-LAN is the one action that has to work while the target is
        // *off*, so the MAC lives in the saved connection, not in the session.
        if (WakeMac.Length == 0 && CurrentProfile is { } profile)
            WakeMac = profile.MacAddress;

        if (session is null)
        {
            _allProcesses.Clear();
            _allServices.Clear();
            Processes.Clear();
            Services.Clear();
            ProcSummary = "";
            SvcSummary = "";
            return;
        }

        await Task.WhenAll(LoadProcessesAsync(session), LoadServicesAsync(session));
    });

    private async Task LoadProcessesAsync(WinnasSession session)
    {
        var list = await RemoteControlService.GetProcessesAsync(session);
        _allProcesses.Clear();
        _allProcesses.AddRange(list);
        RebuildProcesses();

        var total = list.Sum(p => p.Memory);
        ProcSummary = $"{list.Count} 个进程 · 合计占用 {Format.Bytes(total)}";
    }

    private async Task LoadServicesAsync(WinnasSession session)
    {
        var list = await RemoteControlService.GetServicesAsync(session);
        _allServices.Clear();
        _allServices.AddRange(list);
        RebuildServices();

        var running = list.Count(s => s.Running);
        SvcSummary = $"{list.Count} 个服务 · 运行中 {running} · 已停止 {list.Count - running}";
    }

    // ----------------------------------------------------------- filtering

    partial void OnProcFilterChanged(string value) => RebuildProcesses();

    partial void OnSvcFilterChanged(string value) => RebuildServices();

    private void RebuildProcesses()
    {
        var keep = SelectedProcess?.Pid;
        var q = ProcFilter.Trim();
        var view = _allProcesses
            .Where(p => q.Length == 0 ||
                        p.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        p.Pid.ToString().Contains(q, StringComparison.Ordinal) ||
                        (p.CommandLine?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();

        // Refreshing replaces every item with a new instance, so the DataGrid
        // loses its selection. Remember the pid and put it back afterwards,
        // otherwise the buttons grey out under the user's cursor.
        Replace(Processes, view);
        SelectedProcess = Processes.FirstOrDefault(p => p.Pid == keep) ?? Processes.FirstOrDefault();
    }

    private void RebuildServices()
    {
        var keep = SelectedService?.Name;
        var q = SvcFilter.Trim();
        var view = _allServices
            .Where(s => q.Length == 0 ||
                        s.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        s.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();

        Replace(Services, view);
        SelectedService = Services.FirstOrDefault(s => s.Name == keep) ?? Services.FirstOrDefault();
    }

    private static void Replace<T>(ObservableCollection<T> target, List<T> next)
    {
        target.Clear();
        foreach (var item in next) target.Add(item);
    }

    // ------------------------------------------------------------- commands

    [RelayCommand]
    private async Task RefreshProcesses()
    {
        var s = CurrentSession;
        if (s is null) return;
        await Run(async () => await LoadProcessesAsync(s));
    }

    [RelayCommand]
    private async Task RefreshServices()
    {
        var s = CurrentSession;
        if (s is null) return;
        await Run(async () => await LoadServicesAsync(s));
    }

    [RelayCommand]
    private async Task KillProcess()
    {
        var s = CurrentSession;
        var p = SelectedProcess;
        if (s is null || p is null) return;

        if (!DialogService.Confirm(
                $"确定要结束被控端 {s.Profile.Target} 上的进程吗？\n\n" +
                $"{p.Name}（PID {p.Pid}，占用 {p.MemoryText}）\n\n" +
                "结束进程不会保存任何未保存的数据。", "结束进程"))
            return;

        var r = await RemoteControlService.KillProcessAsync(s, p);
        if (r.Ok)
        {
            Notice = r.Message;
            ActivityLog.Warn("远控", "结束进程", $"{s.Profile.Target} · {p.Name} ({p.Pid})");
            await Task.Delay(600);
            await LoadProcessesAsync(s);
        }
        else
        {
            Error = r.Message;
            ActivityLog.Error("远控", "结束进程失败", r.Message);
        }
    }

    [RelayCommand]
    private async Task StartService() => await ServiceAction("start", "启动");

    [RelayCommand]
    private async Task StopService() => await ServiceAction("stop", "停止");

    private async Task ServiceAction(string action, string label)
    {
        var s = CurrentSession;
        var svc = SelectedService;
        if (s is null || svc is null) return;

        if (svc.Critical && action == "stop" &&
            !DialogService.Confirm(
                $"{svc.Name}（{svc.DisplayName}）是让这台机器可被远程管理的关键服务。\n\n" +
                $"停止它与被控端 {s.Profile.Target} 的连接可能会立刻中断，甚至需要去机器前手动恢复。\n\n" +
                "确定要继续吗？", "停止关键服务"))
            return;

        var r = await RemoteControlService.ServiceAsync(s, svc, action);
        Report(r, $"{label}服务 {svc.Name}");

        if (r.Ok)
        {
            await Task.Delay(900);
            await Run(async () => await LoadServicesAsync(s));
        }
    }

    [RelayCommand]
    private async Task RestartService()
    {
        var s = CurrentSession;
        var svc = SelectedService;
        if (s is null || svc is null) return;

        var stop = await RemoteControlService.ServiceAsync(s, svc, "stop");
        if (!stop.Ok)
        {
            Report(stop, $"重启服务 {svc.Name}");
            return;
        }

        // Services do not stop instantly; starting too early returns "already running".
        for (var i = 0; i < 10; i++)
        {
            await Task.Delay(700);
            var current = await RemoteControlService.GetServicesAsync(s);
            var found = current.FirstOrDefault(x => x.Name == svc.Name);
            if (found is null || found.Stopped) break;
        }

        var start = await RemoteControlService.ServiceAsync(s, svc, "start");
        Report(start, $"重启服务 {svc.Name}");
        await Run(async () => await LoadServicesAsync(s));
    }

    [RelayCommand]
    private async Task SetStartMode(string? mode)
    {
        var s = CurrentSession;
        var svc = SelectedService;
        if (s is null || svc is null || string.IsNullOrEmpty(mode)) return;

        var r = await RemoteControlService.SetStartModeAsync(s, svc, mode);
        Report(r, $"修改 {svc.Name} 启动类型");
        if (r.Ok) await Run(async () => await LoadServicesAsync(s));
    }

    // ----------------------------------------------------------- power menu

    [RelayCommand]
    private async Task Power(string? actionText)
    {
        var s = CurrentSession;
        if (s is null || string.IsNullOrEmpty(actionText)) return;

        if (!Enum.TryParse<PowerAction>(actionText, true, out var action)) return;

        if (action is PowerAction.Abort)
        {
            var r = await RemoteControlService.PowerAsync(s, PowerAction.Abort);
            Report(r, "取消被控端的待关机");
            return;
        }

        var label = action switch
        {
            PowerAction.Shutdown => "关闭",
            PowerAction.Reboot => "重启",
            PowerAction.Logoff => "注销",
            PowerAction.Hibernate => "使…休眠",
            PowerAction.Sleep => "使…睡眠",
            PowerAction.Lock => "锁定",
            _ => action.ToString(),
        };

        var extra = action switch
        {
            PowerAction.Sleep or PowerAction.Lock or PowerAction.Hibernate =>
                "\n\n注意：这三项需要被控端存在已登录的交互会话。远程调用时 Windows 的会话隔离可能让它无效，" +
                "这种情况下请用下方的「远程桌面」连上去再操作。",
            _ => "",
        };

        if (!DialogService.Confirm(
                $"确定要{label}被控端 {s.Profile.Target} 吗？\n\n" +
                $"操作：{label}　时间：{DelayText}　强制：{(ForcePower ? "是" : "否")}{extra}",
                "电源操作确认"))
            return;

        var result = await RemoteControlService.PowerAsync(s, action, DelaySeconds, ForcePower, PowerReason);
        Report(result, $"{label}被控端 {s.Profile.Target}");

        if (result.Ok && action is PowerAction.Shutdown or PowerAction.Reboot or PowerAction.Hibernate)
            Notice = result.Message + " 连接会在被控端下线后断开，回到监控页等它起来。";
    }

    // -------------------------------------------------------- remote shell

    [RelayCommand]
    private async Task RunShell()
    {
        var s = CurrentSession;
        if (s is null || CommandRunning) return;

        var text = CommandText.Trim();
        if (text.Length == 0) { Error = "先输入要执行的命令。"; return; }

        CommandRunning = true;
        Error = null;
        CommandStatus = $"正在被控端 {s.Profile.Target} 上执行…";

        try
        {
            var r = await RemoteControlService.RunCommandAsync(s, text, TimeoutSeconds);
            CommandOutput = r.Output.Length == 0
                ? $"（命令已执行，被控端没有产生任何输出）\n{r.Message}"
                : r.Output;
            CommandStatus = r.HeaderText + " · " + r.Message;

            if (r.Ok) Notice = $"命令已在 {s.Profile.Target} 上执行完成";
            ActivityLog.Info("远控", "执行远程命令", $"{s.Profile.Target} · {text}");
        }
        catch (Exception ex)
        {
            Error = OpResult.From(ex).Message;
            CommandStatus = "执行失败";
            ActivityLog.Error("远控", "远程命令失败", OpResult.From(ex).Message);
        }
        finally { CommandRunning = false; }
    }

    [RelayCommand]
    private void UsePreset(CommandPreset? preset)
    {
        if (preset is null) return;
        CommandText = preset.Command;
        Notice = preset.Note;
    }

    [RelayCommand]
    private void ClearOutput()
    {
        CommandOutput = "";
        CommandStatus = "";
    }

    // -------------------------------------------------------------- misc

    [RelayCommand]
    private async Task Notify()
    {
        var s = CurrentSession;
        if (s is null) return;

        var text = NotifyText.Trim();
        if (text.Length == 0) { Error = "先填写要发送的通知内容。"; return; }

        var r = await RemoteControlService.NotifyAsync(s, text);
        Report(r, $"向 {s.Profile.Target} 发送通知");
        if (r.Ok) NotifyText = "";
    }

    [RelayCommand]
    private async Task WakeUp()
    {
        var r = await RemoteControlService.WakeOnLanAsync(WakeMac);
        Report(r, "网络唤醒");
        if (r.Ok)
        {
            var profile = CurrentProfile;
            if (profile is not null)
            {
                profile.MacAddress = WakeMac.Trim();
                AppState.Save();
                Notice = r.Message + " MAC 已保存到当前连接。";
            }
        }
    }

    [RelayCommand]
    private void OpenRemoteDesktop()
    {
        var s = CurrentSession;
        if (s is null) return;

        var r = RemoteControlService.RemoteDesktop(s.Profile.Target);
        Report(r, $"远程桌面 {s.Profile.Target}");
        Notice = r.Ok
            ? r.Message + "（远程桌面在被控端上需要已启用「允许远程连接」）"
            : r.Message;
    }

    private void Report(OpResult r, string what)
    {
        if (r.Ok)
        {
            Notice = r.Message;
            Error = null;
            ActivityLog.Ok("远控", what, r.Message);
        }
        else
        {
            Error = r.Message;
            ActivityLog.Error("远控", what + "失败", r.Message);
        }
    }

    private ConnectionProfile? CurrentProfile =>
        CurrentSession is null ? null : AppState.Find(CurrentSession.Profile.Id);

    partial void OnDelayIndexChanged(int value) => OnPropertyChanged(nameof(DelayText));
}
