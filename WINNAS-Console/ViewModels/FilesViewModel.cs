using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Winnas.Console.Infrastructure;
using Winnas.Console.Models;
using Winnas.Console.Services;

namespace Winnas.Console.ViewModels;

/// <summary>
/// Browse and transfer files on the target. Starts at the share list, then
/// walks into directories. Every path shown to the user is the target's own
/// view of its filesystem (D:\NAS\Public); the UNC rewrite for remote targets
/// happens inside FileService/WinnasService.PathFor.
/// </summary>
public partial class FilesViewModel : PageViewModel
{
    private readonly FileService _files = new();
    private CancellationTokenSource? _transfer;

    [ObservableProperty] private List<FileItem> items = new();

    [ObservableProperty] private List<FileItem> shares = new();

    [ObservableProperty] private string currentPath = "";

    [ObservableProperty] private FileItem? selected;

    [ObservableProperty] private string newFolderName = "";

    [ObservableProperty] private string renameText = "";

    [ObservableProperty] private bool isRenaming;

    [ObservableProperty] private bool transferring;

    [ObservableProperty] private double transferRatio;

    [ObservableProperty] private string? transferText;

    [ObservableProperty] private string downloadDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    [ObservableProperty] private string filter = "";

    public FilesViewModel(WinnasService service) : base(service) { }

    public override string Title => "文件";
    public override string Glyph => "▦";
    public override string Group => "服务";

    public override string HelpText =>
        "地址栏填的是目标机本地路径（例如 D:\\NAS\\Public），不是 UNC——程序会自动转换，回车跳转。" +
        "上传下载是分块流式拷贝，可随时取消；下载同名文件会自动改名，不会覆盖。";

    public bool AtRoot => string.IsNullOrWhiteSpace(CurrentPath);

    private bool UsingVolumes => Shares.Count > 0 && Shares[0].IsVolume;

    /// <summary>Tells the user whether they are looking at shares or raw drives.</summary>
    public string EntryTitle => UsingVolumes ? "本机驱动器（这台机器没有配置共享）" : "从共享开始";

    public string EntryHint => UsingVolumes
        ? "双击驱动器进入。远程管理时通过 C$、D$ 等内置管理共享读取，需要目标机管理员权限。"
        : "双击共享进入。远程管理时通过内置管理共享（D$ 等）读取，需要目标机管理员权限。";

    /// <summary>Case-insensitive name filter over the current directory.</summary>
    public List<FileItem> VisibleItems
    {
        get
        {
            var q = Filter.Trim();
            if (q.Length == 0) return Items;
            return Items.Where(i => i.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        }
    }

    public bool HasFilter => Filter.Trim().Length > 0;

    public bool HasSelection => Selected is not null;

    public bool CanDownload => Selected is { IsDirectory: false };

    public string LocationText => AtRoot ? "共享根目录" : CurrentPath;

    /// <summary>
    /// The address bar is buffered separately from CurrentPath: binding the
    /// box straight to CurrentPath with PropertyChanged would fire a directory
    /// lookup on every keystroke.
    /// </summary>
    public string AddressInput
    {
        get => _addressInput;
        set
        {
            _addressInput = value;
            OnPropertyChanged();
        }
    }

    private string _addressInput = "";

    partial void OnCurrentPathChanged(string value)
    {
        _addressInput = value;
        OnPropertyChanged(nameof(AddressInput));
        OnPropertyChanged(nameof(AtRoot));
        OnPropertyChanged(nameof(LocationText));
    }

    partial void OnItemsChanged(List<FileItem> value) => OnPropertyChanged(nameof(VisibleItems));

    partial void OnFilterChanged(string value)
    {
        OnPropertyChanged(nameof(VisibleItems));
        OnPropertyChanged(nameof(HasFilter));
    }

    public override async Task LoadAsync(WinnasSession? session) => await Run(async () =>
    {
        if (session is null)
        {
            Items = new List<FileItem>();
            Shares = new List<FileItem>();
            return;
        }

        Shares = await _files.ListSharesAsync(session);
        OnPropertyChanged(nameof(EntryTitle));
        OnPropertyChanged(nameof(EntryHint));

        if (AtRoot)
        {
            // Root view IS the share list; no directory listing to do.
            Items = new List<FileItem>();
            return;
        }

        Items = await _files.ListAsync(session, CurrentPath);
        OnPropertyChanged(nameof(AtRoot));
    });

    private async Task OpenPathAsync(string path)
    {
        var session = CurrentSession;
        if (session is null) return;

        CurrentPath = path;
        IsRenaming = false;
        OnPropertyChanged(nameof(AtRoot));
        await LoadAsync(session);
    }

    // ---------------------------------------------------------- navigation

    [RelayCommand]
    private async Task Open(FileItem? item)
    {
        if (item is null) return;
        if (!item.IsDirectory) return;

        // A share root is already an absolute local path on the target, so it
        // navigates exactly like any other directory from here on.
        await OpenPathAsync(item.FullPath);
    }

    [RelayCommand]
    private async Task GoUp()
    {
        if (AtRoot) return;
        var parent = FileService.ParentOf(CurrentPath);
        if (string.IsNullOrEmpty(parent)) { CurrentPath = ""; await LoadAsync(CurrentSession); return; }
        await OpenPathAsync(parent);
    }

    [RelayCommand]
    private void GoHome()
    {
        CurrentPath = "";
        IsRenaming = false;
        OnPropertyChanged(nameof(AtRoot));
        _ = LoadAsync(CurrentSession);
    }

    /// <summary>Enter / 前往 in the address bar.</summary>
    [RelayCommand]
    private async Task GoToAddress()
    {
        var typed = AddressInput.Trim();
        if (typed.Length == 0) return;
        await OpenPathAsync(typed);
    }

    /// <summary>Re-reads the current directory.</summary>
    [RelayCommand]
    private async Task Reload() => await LoadAsync(CurrentSession);

    partial void OnSelectedChanged(FileItem? value)
    {
        RenameText = value?.Name ?? "";
        IsRenaming = false;
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanDownload));
    }

    // ------------------------------------------------------------ mutation

    [RelayCommand]
    private async Task CreateFolder()
    {
        var session = CurrentSession;
        if (session is null) return;
        if (AtRoot) { Error = "请先进一个共享，再新建文件夹"; return; }

        var name = NewFolderName.Trim();
        if (name.Length == 0) { Error = "请输入文件夹名"; return; }

        var result = await _files.CreateFolderAsync(session, CurrentPath, name);
        if (!result.Ok)
        {
            Error = result.Message;
            ActivityLog.Error("文件", $"新建文件夹「{name}」失败", result.Message);
            return;
        }

        ActivityLog.Ok("文件", result.Message, CurrentPath);
        NewFolderName = "";
        Notice = result.Message;
        await LoadAsync(session);
    }

    [RelayCommand]
    private void BeginRename(FileItem? item)
    {
        if (item is null) return;
        Selected = item;
        RenameText = item.Name;
        IsRenaming = true;
    }

    [RelayCommand]
    private void CancelRename() => IsRenaming = false;

    [RelayCommand]
    private async Task CommitRename()
    {
        var session = CurrentSession;
        var item = Selected;
        if (session is null || item is null || !IsRenaming) return;

        var name = RenameText.Trim();
        if (name.Length == 0 || name == item.Name) { IsRenaming = false; return; }

        var result = await _files.RenameAsync(session, item.FullPath, name, item.IsDirectory);
        if (!result.Ok)
        {
            Error = result.Message;
            ActivityLog.Error("文件", $"重命名「{item.Name}」失败", result.Message);
            return;
        }

        ActivityLog.Ok("文件", result.Message, $"{item.Name} → {name}");
        IsRenaming = false;
        Notice = result.Message;
        await LoadAsync(session);
    }

    [RelayCommand]
    private async Task Delete(FileItem? item)
    {
        var session = CurrentSession;
        if (session is null || item is null) return;

        var what = item.IsDirectory ? "文件夹" : "文件";
        if (!DialogService.Confirm(
                $"删除此{what}？\n\n{item.FullPath}\n\n" +
                (item.IsDirectory ? "文件夹内的全部内容都会一起删除。" : "") +
                "此操作不可撤销。",
                $"删除{what}"))
            return;

        var result = await _files.DeleteAsync(session, item.FullPath, item.IsDirectory);
        if (!result.Ok)
        {
            Error = result.Message;
            ActivityLog.Error("文件", $"删除{what}「{item.Name}」失败", result.Message);
            return;
        }

        ActivityLog.Warn("文件", result.Message, item.FullPath);
        Notice = result.Message;
        await LoadAsync(session);
    }

    [RelayCommand]
    private void ClearFilter() => Filter = "";

    // ----------------------------------------------------------- transfer

    [RelayCommand]
    private async Task Upload()
    {
        var session = CurrentSession;
        if (session is null) return;
        if (AtRoot) { Error = "请先进入一个共享目录"; return; }

        var picked = DialogService.PickFiles();
        if (picked.Length == 0) return;

        var progress = new Progress<FileService.TransferProgress>(p =>
        {
            TransferRatio = p.Total <= 0 ? 0 : (double)p.Done / p.Total;
            TransferText = $"{p.Current} · {Format.Bytes(p.Done)} / {Format.Bytes(p.Total)}";
        });

        Transferring = true;
        _transfer = new CancellationTokenSource();

        try
        {
            foreach (var file in picked)
            {
                var result = await _files.UploadAsync(session, file, CurrentPath, progress, _transfer.Token);
                if (!result.Ok)
                {
                    Error = result.Message;
                    ActivityLog.Error("文件", "上传失败", result.Message);
                    break;
                }

                ActivityLog.Ok("文件", result.Message, $"→ {CurrentPath}");
                Notice = result.Message;
            }
        }
        finally
        {
            Transferring = false;
            TransferText = null;
            _transfer = null;
        }

        await LoadAsync(session);
    }

    [RelayCommand]
    private async Task Download(FileItem? item)
    {
        var session = CurrentSession;
        var target = item ?? Selected;
        if (session is null || target is null || target.IsDirectory) return;

        var progress = new Progress<FileService.TransferProgress>(p =>
        {
            TransferRatio = p.Total <= 0 ? 0 : (double)p.Done / p.Total;
            TransferText = $"{p.Current} · {Format.Bytes(p.Done)} / {Format.Bytes(p.Total)}";
        });

        Transferring = true;
        _transfer = new CancellationTokenSource();

        try
        {
            var result = await _files.DownloadAsync(session, target.FullPath, DownloadDir, progress, _transfer.Token);
            if (!result.Ok)
            {
                Error = result.Message;
                ActivityLog.Error("文件", $"下载「{target.Name}」失败", result.Message);
            }
            else
            {
                ActivityLog.Ok("文件", result.Message, $"→ {DownloadDir}");
                Notice = result.Message;
            }
        }
        finally
        {
            Transferring = false;
            TransferText = null;
            _transfer = null;
        }
    }

    [RelayCommand]
    private void CancelTransfer()
    {
        _transfer?.Cancel();
        Transferring = false;
        TransferText = null;
    }

    [RelayCommand]
    private void ChooseDownloadDir()
    {
        var picked = DialogService.PickFolder(DownloadDir);
        if (!string.IsNullOrWhiteSpace(picked)) DownloadDir = picked;
    }
}
