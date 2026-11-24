using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Winnas.Console.Infrastructure;
using Winnas.Console.Models;
using Winnas.Console.Services;

namespace Winnas.Console.ViewModels;

public partial class SharesViewModel : PageViewModel
{
    [ObservableProperty] private List<ShareInfo> allShares = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteShareCommand))]
    private ShareInfo? selected;

    [ObservableProperty] private List<SmbClientInfo> sessions = new();

    [ObservableProperty] private int openFiles;

    [ObservableProperty] private bool showSpecial;

    [ObservableProperty] private bool formVisible;

    [ObservableProperty] private string newName = "";

    [ObservableProperty] private string newPath = "";

    [ObservableProperty] private string newDescription = "";

    [ObservableProperty] private string newAccount = "";

    [ObservableProperty] private bool newReadOnly;

    [ObservableProperty] private string? formError;

    [ObservableProperty] private string filter = "";

    public SharesViewModel(WinnasService service) : base(service) { }

    public override string Title => "共享";
    public override string Glyph => "⇋";
    public override string Group => "服务";

    public override string HelpText =>
        "共享权限明细是通过 MSFT_SmbShare 实例的 GetAccessControlEntries() 拿的——" +
        "MSFT_SmbShareAccess 这个类并不存在。新建共享只设共享权限，不动 NTFS 权限；" +
        "实际生效的是两者中更严格的那个。";

    public List<ShareInfo> VisibleShares
    {
        get
        {
            var q = Filter.Trim();
            return AllShares.Where(s =>
                (ShowSpecial || !s.Special) &&
                (q.Length == 0 ||
                 s.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                 s.Path.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                 s.Description.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();
        }
    }

    public bool HasFilter => Filter.Trim().Length > 0;

    public bool HasSelection => Selected is not null;

    public override async Task LoadAsync(WinnasSession? session) => await Run(async () =>
    {
        if (session is null)
        {
            AllShares = new List<ShareInfo>();
            Sessions = new List<SmbClientInfo>();
            OpenFiles = 0;
            return;
        }
        var snap = await Service.GetSharesAsync(session);
        AllShares = snap.Shares;
        Sessions = snap.Sessions;
        OpenFiles = snap.OpenFiles;
    });

    [RelayCommand]
    private void OpenForm()
    {
        FormVisible = true;
        FormError = null;
    }

    [RelayCommand]
    private void CancelForm()
    {
        FormVisible = false;
        FormError = null;
    }

    [RelayCommand]
    private async Task CreateShare()
    {
        if (!FormVisible) return;
        FormError = null;

        var parent = CurrentSession;
        if (parent is null) { FormError = "未连接"; return; }

        var result = await Service.CreateShareAsync(parent, NewName, NewPath, NewDescription,
            NewReadOnly, NewAccount);

        if (!result.Ok)
        {
            FormError = result.Message;
            ActivityLog.Error("共享", $"创建共享「{NewName}」失败", result.Message);
            return;
        }

        ActivityLog.Ok("共享", result.Message, $"路径 {NewPath} · {(NewReadOnly ? "只读" : "完全控制")}");

        Notice = result.Message;
        NewName = "";
        NewPath = "";
        NewDescription = "";
        NewAccount = "";
        NewReadOnly = false;
        FormVisible = false;

        await LoadAsync(parent);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteShare()
    {
        var session = CurrentSession;
        if (session is null || Selected is null) return;

        var name = Selected.Name;
        if (!DialogService.Confirm($"确定删除共享 {name} 吗？\n\n删除后依赖它的设备会立即断开。", "删除共享"))
            return;

        var result = await Service.DeleteShareAsync(session, name);
        if (!result.Ok)
        {
            Error = result.Message;
            ActivityLog.Error("共享", $"删除共享「{name}」失败", result.Message);
            return;
        }

        ActivityLog.Ok("共享", result.Message);
        Notice = result.Message;
        await LoadAsync(session);
    }

    [RelayCommand]
    private void ClearFilter() => Filter = "";

    [RelayCommand]
    private void Export()
    {
        if (VisibleShares.Count == 0) { Error = "没有可导出的共享"; return; }

        var rows = VisibleShares.Select(s => new object?[]
        {
            s.Name, s.Path, s.Description, s.StateText, s.Special ? "系统" : "用户",
            s.CurrentUsers, s.EncryptData,
            string.Join(" / ", s.Access.Select(a => $"{a.AccountName}:{a.RightText}:{a.TypeText}")),
        });

        var result = Exporter.SaveCsv($"shares-{DateTime.Now:yyyyMMdd-HHmm}.csv",
            new[] { "共享名", "路径", "描述", "状态", "类型", "当前连接", "加密", "权限" },
            rows);

        if (!result.Ok) Error = result.Message;
        else Notice = result.Message;
    }

    partial void OnAllSharesChanged(List<ShareInfo> value)
    {
        // The refresh replaces every ShareInfo instance. Without re-binding the
        // selection to the new object, an auto refresh every few seconds would
        // silently drop whatever the user had highlighted.
        var keep = Selected?.Name;
        if (keep is not null)
            Selected = value.FirstOrDefault(s => string.Equals(s.Name, keep, StringComparison.OrdinalIgnoreCase));

        OnPropertyChanged(nameof(VisibleShares));
    }

    partial void OnShowSpecialChanged(bool value) => OnPropertyChanged(nameof(VisibleShares));

    partial void OnFilterChanged(string value)
    {
        OnPropertyChanged(nameof(VisibleShares));
        OnPropertyChanged(nameof(HasFilter));
    }
}
