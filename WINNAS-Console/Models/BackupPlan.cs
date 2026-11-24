using System.Text.Json.Serialization;
using Winnas.Console.Infrastructure;

namespace Winnas.Console.Models;

/// <summary>
/// A backup definition: where data comes from, where it goes, how robocopy is
/// invoked, when it fires, what has to be true before it runs, and how many
/// old copies are kept.
///
/// Source and target are always written as paths on the managed machine
/// (D:\NAS, E:\NAS-Mirror). The console is only the place where the policy is
/// authored and executed from — it never backs up its own PC.
/// </summary>
public sealed class BackupPlan
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "新备份计划";
    public bool Enabled { get; set; } = true;

    public string Source { get; set; } = "D:\\NAS";
    public string Target { get; set; } = "E:\\NAS-Mirror";

    /// <summary>0 = 镜像(/MIR), 1 = 累积(/E), 2 = 版本目录。</summary>
    public int Mode { get; set; }

    /// <summary>Make a shadow copy of the source volume first, so /MIR cannot
    /// be the only thing standing between a mistaken delete and the data.</summary>
    public bool SnapshotFirst { get; set; } = true;

    public bool CopySecurity { get; set; } = true;
    public int Threads { get; set; } = 8;
    public int Retries { get; set; } = 2;
    public int WaitSeconds { get; set; } = 5;

    /// <summary>Inter-packet gap in ms; 0 means full speed.</summary>
    public int ThrottleMs { get; set; }

    public string ExcludeDirs { get; set; } = "$RECYCLE.BIN,System Volume Information";
    public string ExcludeFiles { get; set; } = "*.tmp,~$*";
    public string ExtraArgs { get; set; } = "";

    // ------------------------------------------------------------- schedule

    /// <summary>0 = 每天, 1 = 每周, 2 = 每月, 3 = 仅手动。</summary>
    public int Frequency { get; set; }
    public int Hour { get; set; } = 3;
    public int Minute { get; set; }
    public int DayOfWeek { get; set; } = 1;
    public int DayOfMonth { get; set; } = 1;

    /// <summary>Kill switch for a copy that should have finished hours ago.</summary>
    public int MaxMinutes { get; set; } = 240;

    // --------------------------------------------------------------- guards

    /// <summary>Refuse to run when the target would end up with less than this
    /// percentage free.</summary>
    public int MinFreePercent { get; set; } = 10;
    public bool SkipIfRunning { get; set; } = true;
    public bool SkipOnBattery { get; set; } = true;

    // ------------------------------------------------- retention (versioned)

    public int KeepDays { get; set; } = 7;
    public int KeepWeeks { get; set; } = 4;
    public int KeepMonths { get; set; } = 12;

    // ------------------------------------------------------------- state

    public DateTime? LastRun { get; set; }
    public DateTime? LastSuccess { get; set; }
    public int LastExitCode { get; set; }
    public string? LastError { get; set; }
    public long LastFiles { get; set; }
    public long LastBytes { get; set; }

    /// <summary>Mirrors whether "WINNAS Backup - &lt;name&gt;" exists in Task Scheduler.</summary>
    public bool Scheduled { get; set; }

    // ------------------------------------------------------------ display

    [JsonIgnore]
    public string ModeText => Mode switch
    {
        0 => "镜像 /MIR",
        1 => "累积 /E",
        2 => "版本目录",
        _ => "未知",
    };

    [JsonIgnore]
    public string ModeHint => Mode switch
    {
        0 => "目标与源完全一致，源删目标也删",
        1 => "只增不改删，源删了目标仍保留",
        2 => "每次一份带时间戳的目录，可回到历史",
        _ => "",
    };

    [JsonIgnore]
    public string FrequencyText => Frequency switch
    {
        0 => $"每天 {Hour:00}:{Minute:00}",
        1 => $"每周{WeekText} {Hour:00}:{Minute:00}",
        2 => $"每月 {DayOfMonth} 日 {Hour:00}:{Minute:00}",
        3 => "仅手动",
        _ => "未知",
    };

    [JsonIgnore]
    public string WeekText => DayOfWeek switch
    {
        0 => "日", 1 => "一", 2 => "二", 3 => "三", 4 => "四", 5 => "五", 6 => "六",
        _ => "?",
    };

    [JsonIgnore]
    public string Summary => $"{Source} → {Target} ｜ {ModeText} ｜ {FrequencyText}";

    [JsonIgnore]
    public string RetentionText => Mode == 2
        ? $"日 {KeepDays} / 周 {KeepWeeks} / 月 {KeepMonths}"
        : "不适用";

    /// <summary>How late a successful run may be before it counts as overdue.</summary>
    [JsonIgnore]
    public TimeSpan? OverdueAfter => Frequency switch
    {
        0 => TimeSpan.FromHours(36),
        1 => TimeSpan.FromDays(9),
        2 => TimeSpan.FromDays(40),
        _ => null,
    };

    [JsonIgnore]
    public bool IsOverdue
    {
        get
        {
            var limit = OverdueAfter;
            if (limit is null || LastSuccess is null) return false;
            return DateTime.Now - LastSuccess.Value > limit.Value;
        }
    }

    [JsonIgnore]
    public string StatusText
    {
        get
        {
            if (!Enabled) return "已停用";
            if (LastError is not null) return "上次失败";
            if (LastSuccess is null) return LastRun is null ? "从未运行" : "尚未成功过";
            return IsOverdue ? "已逾期" : "正常";
        }
    }

    /// <summary>0 fine, 1 attention, 2 broken — drives the row colour.</summary>
    [JsonIgnore]
    public int StatusLevel
    {
        get
        {
            if (!Enabled) return 0;
            if (LastError is not null) return 2;
            if (LastSuccess is null) return LastRun is null ? 0 : 1;
            return IsOverdue ? 2 : 0;
        }
    }

    [JsonIgnore]
    public string LastRunText => LastRun is null ? "—" : LastRun.Value.ToString("MM-dd HH:mm");
    [JsonIgnore]
    public string LastSuccessText => LastSuccess is null ? "—" : LastSuccess.Value.ToString("MM-dd HH:mm");
    [JsonIgnore]
    public string LastSizeText => LastBytes <= 0 ? "—" : Format.Bytes(LastBytes);

    [JsonIgnore]
    public string ScheduledText => Scheduled ? "已注册" : "—";

    [JsonIgnore]
    public string LastDetail
    {
        get
        {
            if (LastSuccess is null) return LastError ?? "还没有成功执行过一次";
            var ago = DateTime.Now - LastSuccess.Value;
            var agoText = ago.TotalDays >= 1
                ? $"{(int)ago.TotalDays} 天前"
                : $"{(int)ago.TotalHours} 小时前";
            return $"上次成功 {LastSuccessText}（{agoText}），复制 {LastFiles} 个文件 / {LastSizeText}";
        }
    }

    public BackupPlan Clone() => (BackupPlan)MemberwiseClone();
}

/// <summary>One row of history. Kept so the console can answer "did it really
/// work last night", which a scheduled task's own last-result never tells
/// you in a readable way.</summary>
public sealed class BackupRun
{
    public string PlanId { get; set; } = "";
    public string PlanName { get; set; } = "";
    public DateTime Started { get; set; } = DateTime.Now;
    public DateTime Finished { get; set; }
    public bool DryRun { get; set; }
    public bool Ok { get; set; }
    public int ExitCode { get; set; }
    public long Files { get; set; }
    public long Bytes { get; set; }
    public long Failed { get; set; }
    public string Message { get; set; } = "";
    public string LogPath { get; set; } = "";

    [JsonIgnore]
    public string StartedText => Started.ToString("MM-dd HH:mm:ss");
    [JsonIgnore]
    public string DurationText
    {
        get
        {
            var span = Finished - Started;
            if (span.TotalSeconds < 1 && span.TotalSeconds >= 0) return "<1 秒";
            return span.TotalMinutes >= 60
                ? $"{(int)span.TotalHours} 小时 {span.Minutes} 分"
                : span.TotalMinutes >= 1
                    ? $"{(int)span.TotalMinutes} 分 {span.Seconds} 秒"
                    : $"{(int)span.TotalSeconds} 秒";
        }
    }

    [JsonIgnore]
    public string KindText => DryRun ? "预演" : "执行";
    [JsonIgnore]
    public string ResultText => Ok ? "成功" : $"失败(0x{ExitCode:X})";
    [JsonIgnore]
    public string SizeText => Bytes <= 0 ? "—" : Format.Bytes(Bytes);
}

/// <summary>One line of the pre-run check panel.</summary>
public sealed class PreflightCheck
{
    public string Name { get; set; } = "";
    public string Detail { get; set; } = "";

    /// <summary>true = good, false = problem.</summary>
    public bool Ok { get; set; } = true;

    /// <summary>A problem that must stop the run, as opposed to advice.</summary>
    public bool Blocking { get; set; } = true;

    public string LevelText => Ok ? "通过" : Blocking ? "阻止" : "注意";
}

/// <summary>What robocopy's closing summary reported.</summary>
public sealed class RobocopySummary
{
    public long Dirs { get; set; }
    public long Files { get; set; }
    public long Bytes { get; set; }
    public long Failed { get; set; }
    public bool Found { get; set; }

    [JsonIgnore]
    public string SizeText => Bytes <= 0 ? "—" : Format.Bytes(Bytes);
}
