using System.Management;
using Winnas.Console.Infrastructure;

namespace Winnas.Console.Models;

// ---------------------------------------------------------------- overview

public sealed class VolumeBrief
{
    public string Drive { get; init; } = "";
    public string Label { get; init; } = "";
    public string FileSystem { get; init; } = "";
    public long Size { get; init; }
    public long Free { get; init; }
    public int Health { get; init; }

    public string HealthText => Health switch
    {
        0 => "健康",
        1 => "警告",
        2 => "异常",
        _ => "未知",
    };

    public long Used => Math.Max(0, Size - Free);
    public double UsedRatio => Size <= 0 ? 0 : (double)Used / Size;
    public string UsageText => $"{Format.Bytes(Used)} / {Format.Bytes(Size)}";
    public string FreeText => Format.Bytes(Free);
}

public sealed class DiskBrief
{
    public string DeviceId { get; init; } = "";
    public string Model { get; init; } = "";
    public long Size { get; init; }
    public string MediaText { get; init; } = "";
    public int Health { get; init; }
    public string HealthText => Health switch
    {
        0 => "健康",
        1 => "警告",
        2 => "异常",
        _ => "未知",
    };
}

public sealed class OverviewInfo
{
    public string HostName { get; init; } = "";
    public string OsCaption { get; init; } = "";
    public string OsVersion { get; init; } = "";
    public string CpuName { get; init; } = "";
    public int CpuLoad { get; init; }
    public int Cores { get; init; }
    public int Threads { get; init; }
    public long MemTotal { get; init; }
    public long MemFree { get; init; }
    public DateTime? BootTime { get; init; }
    public TimeSpan Uptime { get; init; }
    public List<VolumeBrief> Volumes { get; init; } = new();
    public List<DiskBrief> Disks { get; init; } = new();
    public int ShareCount { get; init; }
}

// ---------------------------------------------------------------- storage

public sealed class PartitionBrief
{
    public int DiskNumber { get; init; }
    public string DriveLetter { get; init; } = "";
    public long Size { get; init; }
}

public sealed class DiskInfo
{
    public string DeviceId { get; init; } = "";
    public string Model { get; init; } = "";
    public string Serial { get; init; } = "";
    public string Firmware { get; init; } = "";
    public long Size { get; init; }
    public int MediaType { get; init; }
    public int BusType { get; init; }
    public int Health { get; init; }
    public string Usage { get; init; } = "";
    public int? Temperature { get; init; }
    public int? TemperatureMax { get; init; }
    public long? PowerOnHours { get; init; }
    public long? PowerCycleCount { get; init; }
    public long? ReadErrorsTotal { get; init; }
    public long? WriteErrorsTotal { get; init; }
    public long? Wear { get; init; }
    public List<PartitionBrief> Partitions { get; init; } = new();

    public string MediaText => MediaType switch
    {
        3 => "HDD",
        4 => "SSD",
        5 => "SCM",
        _ => "未指定",
    };

    public string BusText => BusType switch
    {
        1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 4 => "1394", 5 => "SSA",
        6 => "Fibre Channel", 7 => "USB", 8 => "RAID", 9 => "iSCSI",
        10 => "SAS", 11 => "SATA", 12 => "SD", 13 => "MMC",
        14 => "Virtual", 15 => "File Backed Virtual", 16 => "Spaces",
        17 => "NVMe", 18 => "SCM", 19 => "UFS",
        _ => "未知",
    };

    public string HealthText => Health switch
    {
        0 => "健康",
        1 => "警告",
        2 => "异常",
        _ => "未知",
    };
}

// ---------------------------------------------------------------- shares

public sealed class ShareAce
{
    public string AccountName { get; init; } = "";
    public int AccessRight { get; init; }
    public int AccessControlType { get; init; }

    public string RightText => AccessRight switch
    {
        0 => "完全控制",
        1 => "更改",
        2 => "读取",
        _ => "未知",
    };

    public string TypeText => AccessControlType switch
    {
        0 => "允许",
        1 => "拒绝",
        _ => "未知",
    };
}

public sealed class ShareInfo
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public string Description { get; init; } = "";
    public bool EncryptData { get; init; }
    public bool Special { get; init; }
    public int CurrentUsers { get; init; }
    public int ShareState { get; init; }
    public List<ShareAce> Access { get; init; } = new();

    public string StateText => ShareState switch
    {
        1 => "联机",
        2 => "脱机",
        _ => "未知",
    };
}

public sealed class SmbClientInfo
{
    public string Client { get; init; } = "";
    public string User { get; init; } = "";
    public int Opens { get; init; }
}

public sealed class SharesSnapshot
{
    public List<ShareInfo> Shares { get; init; } = new();
    public List<SmbClientInfo> Sessions { get; init; } = new();
    public int OpenFiles { get; init; }
}

// ---------------------------------------------------------------- snapshots

public sealed class SnapshotInfo
{
    public string Id { get; init; } = "";
    public string VolumeName { get; init; } = "";
    public string DeviceObject { get; init; } = "";
    public DateTime? InstallDate { get; init; }
    public bool ClientAccessible { get; init; }
    public bool Persistent { get; init; }
    public bool Differential { get; init; }
}

// ---------------------------------------------------------------- system

public sealed class ServiceInfo
{
    public string Name { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string State { get; init; } = "";
    public string StartMode { get; init; } = "";
}

public sealed class IpInfo
{
    public string Interface { get; init; } = "";
    public string Address { get; init; } = "";
    public int Prefix { get; init; }
}

public sealed class RouteInfo
{
    public string Interface { get; init; } = "";
    public string Gateway { get; init; } = "";
    public int Metric { get; init; }
}

public sealed class ProcInfo
{
    public string Name { get; init; } = "";
    public int Pid { get; init; }
    public long Memory { get; init; }
}

public sealed class SystemInfo
{
    public List<ServiceInfo> Services { get; init; } = new();
    public List<IpInfo> Addresses { get; init; } = new();
    public List<RouteInfo> Routes { get; init; } = new();
    public List<ProcInfo> TopProcesses { get; init; } = new();
}

// ---------------------------------------------------------------- backup

public sealed class TaskInfo
{
    public string Path { get; init; } = "";
    public int State { get; init; }
    public bool Enabled { get; init; }
    public DateTime? LastRun { get; init; }
    public DateTime? NextRun { get; init; }
    public long LastResult { get; init; }

    public string StateText => State switch
    {
        0 => "未知",
        1 => "已禁用",
        2 => "排队",
        3 => "就绪",
        4 => "运行中",
        _ => "未知",
    };

    public string ResultText => LastResult switch
    {
        0 => "成功",
        267009 => "仍在运行",
        _ => "0x" + ((uint)LastResult).ToString("X8"),
    };
}

public sealed class LogFileInfo
{
    public string Name { get; init; } = "";
    public long Size { get; init; }
    public DateTime? LastWrite { get; init; }
}

public sealed class BackupInfo
{
    public TaskInfo? Task { get; init; }
    public bool RobocopyRunning { get; init; }
    public long? SourceFiles { get; init; }
    public long? SourceBytes { get; init; }
    public bool SourceStale { get; init; }
    public List<LogFileInfo> Logs { get; init; } = new();
    public List<VolumeBrief> Volumes { get; init; } = new();
}

// ------------------------------------------------------------ live metrics

/// <summary>
/// One point of the rolling rate chart. Rates are bytes per second, as
/// reported directly by the Win32_PerfFormattedData classes.
/// </summary>
public sealed class PerfSample
{
    public DateTime Time { get; init; }
    public int CpuLoad { get; init; }
    public long DiskRead { get; init; }
    public long DiskWrite { get; init; }
    public long NetIn { get; init; }
    public long NetOut { get; init; }
    public long NetCapacity { get; init; }
}

// -------------------------------------------------------- snapshot policy

/// <summary>Saved in AppSettings, so it needs settable properties.</summary>
public sealed class SnapshotPolicy
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Volume { get; set; } = "D:\\";
    public bool Enabled { get; set; } = true;

    /// <summary>0 = 每天, 1 = 每周, 2 = 每小时.</summary>
    public int Frequency { get; set; }

    public int Hour { get; set; } = 3;
    public int Minute { get; set; }
    public int DayOfWeek { get; set; } = 1;
    public int Keep { get; set; } = 5;
    public DateTime? LastRun { get; set; }
    public string? LastError { get; set; }

    public string VolumeDisplay => Volume;

    public string FrequencyText => Frequency switch
    {
        0 => $"每天 {Hour:00}:{Minute:00}",
        1 => $"每周{WeekText} {Hour:00}:{Minute:00}",
        2 => $"每小时第 {Minute:00} 分",
        _ => "未知",
    };

    public string WeekText => DayOfWeek switch
    {
        0 => "日", 1 => "一", 2 => "二", 3 => "三", 4 => "四", 5 => "五", 6 => "六",
        _ => "?",
    };

    public string Summary => $"{Volume} · {FrequencyText} · 保留 {Keep} 个";
}

// ---------------------------------------------------------------- chat

/// <summary>
/// Content changes while a reply streams in, so the row has to notify —
/// an ObservableCollection alone would not repaint the bubble.
/// </summary>
public sealed class ChatMessage : System.ComponentModel.INotifyPropertyChanged
{
    private string _content = "";
    private bool _streaming;

    public string Role { get; init; } = "user";
    public DateTime Time { get; init; } = DateTime.Now;

    public string Content
    {
        get => _content;
        set { _content = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Content))); }
    }

    public bool Streaming
    {
        get => _streaming;
        set { _streaming = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Streaming))); }
    }

    public bool IsUser => Role == "user";
    public string TimeText => Time.ToString("HH:mm:ss");

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

// ---------------------------------------------------------------- files

public sealed class FileItem
{
    public string Name { get; init; } = "";
    public string FullPath { get; init; } = "";
    public bool IsDirectory { get; init; }
    public bool IsShareRoot { get; init; }
    public bool IsVolume { get; init; }
    public long Size { get; init; }
    public DateTime? LastWrite { get; init; }

    public string Glyph => IsDirectory ? "▣" : "▢";
    public string KindText => IsDirectory ? "文件夹" : ExtensionText;
    public string SizeText => IsDirectory ? "" : Format.Bytes(Size);

    private string ExtensionText
    {
        get
        {
            var dot = Name.LastIndexOf('.');
            if (dot <= 0 || dot == Name.Length - 1) return "文件";
            return Name[(dot + 1)..].ToUpperInvariant() + " 文件";
        }
    }
}

// ---------------------------------------------------------------- result

public sealed class OpResult
{
    public bool Ok { get; init; }
    public string Message { get; init; } = "";

    public static OpResult Good(string msg = "完成") => new() { Ok = true, Message = msg };
    public static OpResult Fail(string msg) => new() { Ok = false, Message = msg };

    public static OpResult From(Exception ex) =>
        new() { Ok = false, Message = Friendly(ex) };

    private static string Friendly(Exception ex)
    {
        var msg = ex.Message.Trim();

        // Classic WMI reports the interesting failures two ways: a
        // ManagementException with a ManagementStatus code, or a bare
        // COMException carrying the HRESULT. Both used to say "拒绝访问" and
        // nothing else, which is what sent people looking for a password
        // problem that did not exist.
        var accessDenied =
            ex is ManagementException { ErrorCode: ManagementStatus.AccessDenied } ||
            ex is System.Runtime.InteropServices.COMException { HResult: unchecked((int)0x80070005) } ||
            ex is UnauthorizedAccessException ||
            msg.Contains("拒绝访问", StringComparison.Ordinal) ||
            msg.Contains("Access is denied", StringComparison.OrdinalIgnoreCase);

        if (accessDenied)
            return "访问被拒绝。三种常见原因：①账户或口令不对（工作组环境请把用户名写成 主机名\\账户）；" +
                   "②该账户不在目标机的 Administrators 组；" +
                   "③目标机未设置 LocalAccountTokenFilterPolicy=1，本地账户的远程连接被系统降权。" +
                   "点「连接」页的「连接诊断」可以逐项定位到具体是哪一条。";

        if (ex is System.Runtime.InteropServices.COMException { HResult: unchecked((int)0x800706BA) } ||
            msg.Contains("RPC", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("0x800706BA", StringComparison.OrdinalIgnoreCase))
            return "无法连接目标（RPC 不可用）。请检查主机名、网络连通性，以及目标机防火墙是否放行「Windows Management Instrumentation (DCOM-In)」。";

        if (ex is ManagementException { ErrorCode: ManagementStatus.InvalidNamespace })
            return "目标的 WMI 里没有这个命名空间，可能不是 Windows 或 WMI 已损坏。";

        if (msg.Length > 220) msg = msg[..220] + "...";
        return msg;
    }
}
