using Winnas.Console.Infrastructure;
using Winnas.Console.Models;

namespace Winnas.Console.Services;

public enum Severity
{
    Info,
    Warn,
    Critical,
}

public sealed class HealthFinding
{
    public Severity Severity { get; init; }
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Advice { get; init; } = "";

    public string SeverityText => Severity switch
    {
        Severity.Critical => "严重",
        Severity.Warn => "警告",
        _ => "提示",
    };
}

public sealed class HealthReport
{
    public int Score { get; init; } = 100;
    public List<HealthFinding> Findings { get; init; } = new();

    public int CriticalCount => Findings.Count(f => f.Severity == Severity.Critical);
    public int WarnCount => Findings.Count(f => f.Severity == Severity.Warn);

    public string ScoreText => Score >= 90 ? "良好" : Score >= 70 ? "需要关注" : "存在风险";

    public string SummaryText => Findings.Count == 0
        ? "未发现需要立即处理的问题"
        : $"{CriticalCount} 项严重 · {WarnCount} 项警告";
}

/// <summary>
/// Turns raw CIM data into "what should I actually do about this".
///
/// The point is not to restate the numbers the page already shows — it is to
/// cross-reference them: a disk that is hot AND has read errors is a different
/// story from a disk that is merely full.
/// </summary>
public static class HealthCheck
{
    private const int FullWarn = 80;
    private const int FullCritical = 92;
    private const int TempWarn = 60;
    private const int TempCritical = 70;

    public static async Task<HealthReport> InspectAsync(WinnasSession s, WinnasService service)
    {
        var findings = new List<HealthFinding>();
        var score = 100;

        var overview = await service.GetOverviewAsync(s);
        var disks = await service.GetDisksAsync(s);

        // ---------------------------------------------------------- capacity
        foreach (var v in overview.Volumes)
        {
            if (v.Size <= 0) continue;
            var used = v.UsedRatio;
            if (used * 100 >= FullCritical)
            {
                score -= 15;
                findings.Add(new HealthFinding
                {
                    Severity = Severity.Critical,
                    Title = $"{v.Drive} 盘即将写满（{used * 100:0.#}%）",
                    Detail = $"{v.Label} 已用 {Format.Bytes(v.Used)} / {Format.Bytes(v.Size)}，剩余 {Format.Bytes(v.Free)}。",
                    Advice = "先清掉回收站、临时目录与旧备份；卷影副本也会占用空间，可在「快照」页清理。写满后 SMB 写入与卷影副本都会失败。",
                });
            }
            else if (used * 100 >= FullWarn)
            {
                score -= 6;
                findings.Add(new HealthFinding
                {
                    Severity = Severity.Warn,
                    Title = $"{v.Drive} 盘使用率偏高（{used * 100:0.#}%）",
                    Detail = $"{v.Label} 剩余 {Format.Bytes(v.Free)}。",
                    Advice = "留意增长趋势。若持续增长，考虑扩容或迁移冷数据。",
                });
            }
        }

        // ------------------------------------------------------------- disks
        foreach (var d in disks)
        {
            var label = string.IsNullOrWhiteSpace(d.Model) ? $"磁盘 {d.DeviceId}" : d.Model;

            if (d.Health != 0)
            {
                score -= 20;
                findings.Add(new HealthFinding
                {
                    Severity = d.Health == 2 ? Severity.Critical : Severity.Warn,
                    Title = $"{label} 健康状态：{d.HealthText}",
                    Detail = $"序列号 {d.Serial}，总线 {d.BusText}，容量 {Format.Bytes(d.Size)}。",
                    Advice = "立即把数据备份到另一块盘。SMART 告警意味着盘可能随时失效，继续写入会加速损坏。",
                });
            }

            if (d.Temperature is >= TempCritical)
            {
                score -= 10;
                findings.Add(new HealthFinding
                {
                    Severity = Severity.Critical,
                    Title = $"{label} 温度过高（{Format.Temp(d.Temperature.Value)}）",
                    Detail = $"厂商记录的最高温度 {Format.Temp(d.TemperatureMax ?? 0)}。",
                    Advice = "检查机箱风道与风扇。长期高温会显著缩短硬盘寿命。",
                });
            }
            else if (d.Temperature is >= TempWarn)
            {
                score -= 4;
                findings.Add(new HealthFinding
                {
                    Severity = Severity.Warn,
                    Title = $"{label} 温度偏高（{Format.Temp(d.Temperature.Value)}）",
                    Detail = "还没有到危险区间，但值得留意。",
                    Advice = "改善通风，或把持续高负载任务挪到别的盘。",
                });
            }

            var ioErrors = (d.ReadErrorsTotal ?? 0) + (d.WriteErrorsTotal ?? 0);
            if (ioErrors > 0)
            {
                score -= 8;
                findings.Add(new HealthFinding
                {
                    Severity = Severity.Warn,
                    Title = $"{label} 出现 {ioErrors} 次读写错误",
                    Detail = $"读错误 {d.ReadErrorsTotal ?? 0}，写错误 {d.WriteErrorsTotal ?? 0}。",
                    Advice = "读写错误通常先于彻底失效出现。尽快备份并检查线缆/SATA 口。",
                });
            }

            if (d.Wear is >= 90)
            {
                score -= 12;
                findings.Add(new HealthFinding
                {
                    Severity = Severity.Critical,
                    Title = $"{label} 磨损已达 {d.Wear}%（SSD）",
                    Detail = "接近标称写入寿命。",
                    Advice = "准备替换盘。SSD 磨损到 100% 后可能直接变为只读。",
                });
            }
        }

        // --------------------------------------------------------- snapshots
        var snapshots = await service.GetSnapshotsAsync(s);
        if (snapshots.Count == 0)
        {
            score -= 8;
            findings.Add(new HealthFinding
            {
                Severity = Severity.Warn,
                Title = "这台机器上没有任何卷影副本",
                Detail = "误删文件或被勒索软件加密后没有可回滚的点。",
                Advice = "到「快照」页给数据盘配一条定时策略，并点一次「注册自动执行」——只加策略不注册是不会跑的。",
            });
        }
        else
        {
            var newest = snapshots.Max(x => x.InstallDate);
            var age = newest is null ? TimeSpan.MaxValue : DateTime.Now - newest.Value;
            if (age > TimeSpan.FromDays(14))
            {
                score -= 5;
                findings.Add(new HealthFinding
                {
                    Severity = Severity.Warn,
                    Title = $"最近的快照已过去 {(int)age.TotalDays} 天",
                    Detail = $"最新快照时间：{Format.Date(newest)}。",
                    Advice = "快照的价值取决于新鲜度。检查定时策略是否真的在跑（计划任务有没有被注册）。",
                });
            }
        }

        var policies = AppState.Settings.SnapshotPolicies;
        if (policies.Count == 0)
        {
            findings.Add(new HealthFinding
            {
                Severity = Severity.Info,
                Title = "还没有配置定时快照策略",
                Detail = "目前只能手动创建快照。",
                Advice = "「快照」页可以加策略：选卷、选频率、设保留数量，然后注册计划任务。",
            });
        }
        else if (!SnapshotPolicyService.IsTaskRegistered(s))
        {
            score -= 6;
            findings.Add(new HealthFinding
            {
                Severity = Severity.Warn,
                Title = $"已配置 {policies.Count} 条快照策略，但计划任务未注册",
                Detail = "策略只会存档，不会自动执行。",
                Advice = "在「快照」页点「注册自动执行」。这需要一次管理员权限。",
            });
        }

        // ------------------------------------------------------------ backup
        var backup = await service.GetBackupAsync(s);
        if (backup.Task is null)
        {
            score -= 10;
            findings.Add(new HealthFinding
            {
                Severity = Severity.Warn,
                Title = $"找不到备份任务「{AppState.Settings.BackupTask}」",
                Detail = "可能是任务名填错，或它建在另一台机器上。",
                Advice = "到「备份」页的设置里核对任务名（要带完整路径，例如 \\NAS Mirror\\Daily）。改完记得保存。",
            });
        }
        else
        {
            if (!backup.Task.Enabled)
            {
                score -= 12;
                findings.Add(new HealthFinding
                {
                    Severity = Severity.Critical,
                    Title = "备份任务当前是禁用状态",
                    Detail = $"任务路径 {backup.Task.Path}。",
                    Advice = "到「备份」页确认，或在目标机上用任务计划程序启用它。禁用状态下的备份等于没有备份。",
                });
            }

            if (backup.Task.LastRun is not null && backup.Task.LastResult != 0 && backup.Task.LastResult != 267009)
            {
                score -= 10;
                findings.Add(new HealthFinding
                {
                    Severity = Severity.Warn,
                    Title = $"上次备份未成功（{backup.Task.ResultText}）",
                    Detail = $"上次运行：{Format.Date(backup.Task.LastRun)}。",
                    Advice = "到「备份」页打开最新一份日志看具体失败原因；robocopy 的返回码小于 8 通常算成功。",
                });
            }

            if (backup.Task.LastRun is not null && DateTime.Now - backup.Task.LastRun.Value > TimeSpan.FromDays(3))
            {
                score -= 5;
                findings.Add(new HealthFinding
                {
                    Severity = Severity.Warn,
                    Title = $"备份已 {(int)(DateTime.Now - backup.Task.LastRun.Value).TotalDays} 天没跑",
                    Detail = $"上次运行：{Format.Date(backup.Task.LastRun)}。",
                    Advice = "确认计划任务的触发器还在，以及机器在计划时间点是否开机。",
                });
            }
        }

        // ------------------------------------------------------------ shares
        var shareSnap = await service.GetSharesAsync(s);
        foreach (var sh in shareSnap.Shares.Where(x => !x.Special))
        {
            var risky = sh.Access
                .Where(a => a.AccessControlType == 0 && a.AccessRight == 0 &&
                            a.AccountName.Equals("Everyone", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (risky.Count > 0)
            {
                score -= 5;
                findings.Add(new HealthFinding
                {
                    Severity = Severity.Warn,
                    Title = $"共享「{sh.Name}」对 Everyone 开放了完全控制",
                    Detail = $"路径 {sh.Path}。同一网段内任何人都能读写删除。",
                    Advice = "改成给具体账号授权：删掉 Everyone，加指定用户并按需给「读取」。注意 NTFS 权限是第二道闸，两处都要收敛。",
                });
            }
        }

        // ------------------------------------------------------------ memory
        if (overview.MemTotal > 0)
        {
            var memUsed = 1.0 - (double)overview.MemFree / overview.MemTotal;
            if (memUsed >= 0.92)
            {
                score -= 6;
                findings.Add(new HealthFinding
                {
                    Severity = Severity.Warn,
                    Title = $"内存占用 {memUsed * 100:0.#}%",
                    Detail = $"已用 {Format.Bytes(overview.MemTotal - overview.MemFree)} / {Format.Bytes(overview.MemTotal)}。",
                    Advice = "到「系统」页看 Top 进程。若是搜索索引或杀毒软件常驻占用，可考虑调整其扫描计划。",
                });
            }
        }

        score = Math.Clamp(score, 0, 100);

        var order = new Dictionary<Severity, int>
        {
            [Severity.Critical] = 0, [Severity.Warn] = 1, [Severity.Info] = 2,
        };
        findings.Sort((a, b) => order[a.Severity].CompareTo(order[b.Severity]));

        return new HealthReport { Score = score, Findings = findings };
    }
}
