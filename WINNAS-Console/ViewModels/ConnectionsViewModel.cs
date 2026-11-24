using System.Collections.ObjectModel;
using System.Security;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Winnas.Console.Infrastructure;
using Winnas.Console.Models;
using Winnas.Console.Services;

namespace Winnas.Console.ViewModels;

public partial class ConnectionsViewModel : PageViewModel
{
    private readonly Func<ConnectionProfile, Task> _connect;
    private readonly Action _disconnect;
    private readonly Action _reloadProfiles;
    private readonly Action _openSettings;
    private SecureString? _pendingPassword;

    /// <summary>The run the AI button reasons about — kept so both buttons
    /// always talk about the same evidence.</summary>
    private List<DiagItem> _lastRun = new();
    private ConnectionProfile? _lastProfile;
    private CancellationTokenSource? _aiCts;

    public ObservableCollection<ConnectionProfile> Profiles { get; } = new();

    [ObservableProperty] private ConnectionProfile? selected;

    [ObservableProperty] private string editName = "";

    [ObservableProperty] private string editComputer = "";

    [ObservableProperty] private int editTransportIndex;

    [ObservableProperty] private string editUser = "";

    [ObservableProperty] private string editDomain = "";

    [ObservableProperty] private bool rememberPassword;

    [ObservableProperty] private string? hint;

    /// <summary>Results of the last "连接诊断" run, shown inline under the form.</summary>
    public ObservableCollection<DiagItem> Diagnostics { get; } = new();

    [ObservableProperty] private bool diagVisible;

    [ObservableProperty] private bool diagRunning;

    /// <summary>The model's reading of the last run, streamed in.</summary>
    [ObservableProperty] private string aiText = "";

    [ObservableProperty] private bool aiRunning;

    [ObservableProperty] private bool aiVisible;

    /// <summary>True when a key is stored, so the button can say why it will
    /// not work instead of failing after the click.</summary>
    public bool AiConfigured => SecretVault.UnprotectText(AppState.Settings.AiApiKey).Length > 0;

    public ConnectionsViewModel(WinnasService service,
        Func<ConnectionProfile, Task> connect,
        Action disconnect,
        Action reloadProfiles,
        Action openSettings) : base(service)
    {
        _connect = connect;
        _disconnect = disconnect;
        _reloadProfiles = reloadProfiles;
        _openSettings = openSettings;
    }

    public override string Title => "连接";
    public override string Glyph => "⚡";
    public override string Group => "系统";

    public override string HelpText =>
        "远程 DCOM 可以填目标机的管理员账户（留空则用当前 Windows 账户），目标机只需放行 WMI 防火墙规则；" +
        "远程 WinRM 需目标机先跑 winrm quickconfig。密码用 DPAPI 按当前用户加密，换 Windows 账号后需重填。" +
        "连不上就点「连接诊断」，它会逐项指出卡在哪一层。";

    /// <summary>Nothing to poll: this page is pure configuration.</summary>
    public override Task LoadAsync(WinnasSession? session) => Task.CompletedTask;

    public void LoadProfiles()
    {
        Profiles.Clear();
        foreach (var p in AppState.Settings.Connections) Profiles.Add(p.Clone());

        if (Selected is not null && Profiles.Any(p => p.Id == Selected.Id))
        {
            var keep = Profiles.First(p => p.Id == Selected.Id);
            Selected = keep;
        }
        else
        {
            Selected = Profiles.FirstOrDefault();
        }
    }

    public void SetPendingPassword(SecureString pwd)
    {
        _pendingPassword = pwd.Length == 0 ? null : pwd.Copy();
        _pendingPassword?.MakeReadOnly();
    }

    [RelayCommand]
    private void NewProfile()
    {
        Selected = null;
        EditName = "";
        EditComputer = "";
        EditTransportIndex = 0;
        EditUser = "";
        EditDomain = "";
        RememberPassword = false;
        _pendingPassword = null;
        Hint = "新建连接：留空主机名并选择「本机」即为本地管理。";
    }

    [RelayCommand]
    private void Save()
    {
        if (string.IsNullOrWhiteSpace(EditName)) { Hint = "请填写名称"; return; }

        var isNew = Selected is null;
        var id = Selected?.Id ?? Guid.NewGuid().ToString("N");

        var profile = new ConnectionProfile
        {
            Id = id,
            Name = EditName.Trim(),
            Computer = EditComputer.Trim(),
            Transport = IndexToTransport(EditTransportIndex),
            UserName = EditUser.Trim(),
            Domain = EditDomain.Trim(),
            ProtectedPassword = Selected?.ProtectedPassword,
        };

        if (_pendingPassword is not null) profile.SetPassword(_pendingPassword);
        else if (!RememberPassword) profile.ProtectedPassword = null;

        if (profile.Transport == TransportKind.Local) profile.Computer = "";

        AppState.Upsert(profile);
        _pendingPassword = null;
        _reloadProfiles();

        Selected = Profiles.FirstOrDefault(p => p.Id == id);
        Hint = isNew ? "已保存新连接" : "已保存";
    }

    [RelayCommand]
    private void Remove()
    {
        if (Selected is null) { Hint = "请先选择一条连接"; return; }

        if (!DialogService.Confirm($"删除连接「{Selected.Name}」？", "删除连接")) return;

        AppState.Remove(Selected.Id);
        _reloadProfiles();
        Hint = "已删除";
    }

    /// <summary>
    /// Tests exactly what is currently typed into the form, saved or not, so a
    /// wrong password can be found before it becomes a confusing "access
    /// denied" on the monitoring pages.
    /// </summary>
    [RelayCommand]
    private async Task Diagnose()
    {
        var profile = BuildFromForm();
        if (string.IsNullOrWhiteSpace(profile.Computer) && !profile.IsLocal)
        {
            Hint = "先填写主机名或 IP 再诊断。";
            return;
        }

        DiagVisible = true;
        DiagRunning = true;
        Diagnostics.Clear();
        Hint = null;

        try
        {
            var items = await ConnectionDiagnostics.RunAsync(profile);
            _lastRun = items;
            _lastProfile = profile;
            AiText = "";
            AiVisible = false;

            foreach (var item in items) Diagnostics.Add(item);

            var fails = items.Count(i => i.Level == DiagLevel.Fail);
            Hint = fails == 0
                ? "诊断通过，可以点「连接到此主机」。"
                : $"发现 {fails} 个问题，按红色条目下面的命令在被控端处理。";
        }
        catch (Exception ex)
        {
            Hint = "诊断失败：" + OpResult.From(ex).Message;
        }
        finally { DiagRunning = false; }
    }

    /// <summary>
    /// Asks the configured model to read the run. The prompt is built from the
    /// same rows the user is looking at, so the answer cannot drift into
    /// generic advice about a problem they do not have.
    /// </summary>
    [RelayCommand]
    private async Task AiDiagnose()
    {
        if (_lastRun.Count == 0)
        {
            Hint = "先点「连接诊断」跑一次，再让 AI 分析。";
            return;
        }

        if (!AiConfigured)
        {
            Hint = "还没有配置 AI API Key，已跳到「设置 → AI 助手」。填好 Key 再回来点一次。";
            _openSettings();
            return;
        }

        _aiCts?.Cancel();
        _aiCts = new CancellationTokenSource();

        AiText = "";
        AiVisible = true;
        AiRunning = true;
        Hint = $"正在让 {AppState.Settings.AiModel} 分析诊断结果…";

        try
        {
            await foreach (var chunk in AiDiagnoseService.StreamAsync(
                               _lastProfile ?? BuildFromForm(), _lastRun, _aiCts.Token))
                AiText += chunk;

            Hint = "AI 分析完成。结论仅供参照，命令请先看清楚再执行。";
            ActivityLog.Info("连接", "AI 诊断", $"模型 {AppState.Settings.AiModel}");
        }
        catch (OperationCanceledException)
        {
            Hint = "已停止 AI 分析。";
        }
        catch (Exception ex)
        {
            var msg = OpResult.From(ex).Message;
            AiText = "AI 分析失败：" + msg;
            Hint = "检查「设置 → AI 助手」里的地址、模型与 Key，可点「测试连接」验证。";
            ActivityLog.Error("连接", "AI 诊断失败", msg);
        }
        finally
        {
            AiRunning = false;
        }
    }

    [RelayCommand]
    private void StopAi()
    {
        _aiCts?.Cancel();
    }

    /// <summary>
    /// Copies a script to run ON the NAS. Useful exactly when the console
    /// cannot see far enough — which is the case whenever authentication fails.
    /// </summary>
    [RelayCommand]
    private void CopyTargetScript()
    {
        try
        {
            var script = AiDiagnoseService.BuildTargetScript(BuildFromForm());
            System.Windows.Clipboard.SetText(script);
            Hint = "排查脚本已复制。到 NAS 上以管理员运行，粘贴执行，把输出贴回给 AI。";
            ActivityLog.Info("连接", "复制被控端排查脚本");
        }
        catch (Exception ex)
        {
            Hint = "复制失败：" + OpResult.From(ex).Message;
        }
    }

    [RelayCommand]
    private async Task Connect() => await _connect(BuildFromForm());

    [RelayCommand]
    private void Disconnect() => _disconnect();

    private ConnectionProfile BuildFromForm()
    {
        var profile = new ConnectionProfile
        {
            Id = Selected?.Id ?? Guid.NewGuid().ToString("N"),
            Name = EditName.Trim().Length == 0 ? "临时连接" : EditName.Trim(),
            Computer = EditComputer.Trim(),
            Transport = IndexToTransport(EditTransportIndex),
            UserName = EditUser.Trim(),
            Domain = EditDomain.Trim(),
            ProtectedPassword = Selected?.ProtectedPassword,
        };

        if (_pendingPassword is not null) profile.SetPassword(_pendingPassword);
        return profile;
    }

    // Two choices only: the WinRM option was removed when the remote layer
    // moved to classic WMI. An old profile saved as WSMan opens here as 1 and
    // is rewritten to DCom the next time it is saved.
    private static TransportKind IndexToTransport(int index) =>
        index == 1 ? TransportKind.DCom : TransportKind.Local;

    partial void OnSelectedChanged(ConnectionProfile? value)
    {
        if (value is null)
        {
            EditName = "";
            EditComputer = "";
            EditTransportIndex = 0;
            EditUser = "";
            EditDomain = "";
            RememberPassword = false;
            _pendingPassword = null;
            return;
        }

        EditName = value.Name;
        EditComputer = value.Computer;
        EditTransportIndex = value.Transport == TransportKind.Local ? 0 : 1;
        EditUser = value.UserName;
        EditDomain = value.Domain;
        RememberPassword = value.ProtectedPassword is not null;
        _pendingPassword = null;
    }
}
