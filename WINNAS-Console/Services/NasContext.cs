using System.Text;
using Winnas.Console.Infrastructure;
using Winnas.Console.Models;

namespace Winnas.Console.Services;

/// <summary>
/// Flattens the live machine state into a short text brief for the AI.
///
/// Deliberately lossy: a full dump would blow the context window on every
/// question, so this keeps the numbers an operator would actually quote and
/// caps every list.
/// </summary>
public static class NasContext
{
    private const int MaxShares = 20;
    private const int MaxSnapshots = 10;

    public static async Task<string> BuildAsync(WinnasSession? session, WinnasService service)
    {
        if (session is null) return "（当前没有连接任何主机，无法读取状态）";

        var sb = new StringBuilder();
        sb.AppendLine($"# 目标主机：{session.Label}（{session.Profile.TransportText}）");
        sb.AppendLine($"采集时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        try
        {
            var ov = await service.GetOverviewAsync(session);
            sb.AppendLine();
            sb.AppendLine("## 系统");
            sb.AppendLine($"- 操作系统：{ov.OsCaption} {ov.OsVersion}");
            sb.AppendLine($"- CPU：{ov.CpuName}（{ov.Cores} 核 {ov.Threads} 线程）当前负载 {ov.CpuLoad}%");
            sb.AppendLine($"- 内存：已用 {Format.Bytes(ov.MemTotal - ov.MemFree)} / 共 {Format.Bytes(ov.MemTotal)}");
            sb.AppendLine($"- 已运行：{Format.Uptime(ov.Uptime)}（开机于 {Format.Date(ov.BootTime)}）");

            if (ov.Volumes.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## 卷");
                foreach (var v in ov.Volumes)
                    sb.AppendLine($"- {v.Drive} {v.Label} {v.FileSystem}：已用 {v.UsageText}，" +
                                  $"剩余 {v.FreeText}（{v.UsedRatio * 100:0.#}%），状态 {v.HealthText}");
            }

            if (ov.Disks.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## 物理磁盘");
                foreach (var d in ov.Disks)
                    sb.AppendLine($"- 磁盘 {d.DeviceId} {d.Model} {d.MediaText} {Format.Bytes(d.Size)}，{d.HealthText}");
            }
        }
        catch (Exception ex) { sb.AppendLine($"\n（概览采集失败：{ex.Message}）"); }

        try
        {
            var disks = await service.GetDisksAsync(session);
            if (disks.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## 磁盘健康明细");
                foreach (var d in disks)
                {
                    var temp = d.Temperature is null ? "—" : Format.Temp(d.Temperature);
                    var hours = d.PowerOnHours is null ? "—" : Format.Hours(d.PowerOnHours);
                    sb.AppendLine($"- 磁盘 {d.DeviceId} {d.Model}：{d.BusText}/{d.MediaText}，" +
                                  $"{d.HealthText}，温度 {temp}，通电 {hours}，" +
                                  $"读错误 {d.ReadErrorsTotal?.ToString() ?? "—"}，" +
                                  $"写错误 {d.WriteErrorsTotal?.ToString() ?? "—"}");
                }
            }
        }
        catch { /* optional */ }

        try
        {
            var shares = await service.GetSharesAsync(session);
            var visible = shares.Shares.Where(x => !x.Special).Take(MaxShares).ToList();
            if (visible.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## SMB 共享");
                foreach (var s in visible)
                {
                    var acl = s.Access.Count == 0
                        ? "（无权限条目）"
                        : string.Join("、", s.Access.Select(a => $"{a.AccountName} {a.RightText}（{a.TypeText}）"));
                    sb.AppendLine($"- \\\\{session.Profile.Target}\\{s.Name} → {s.Path}；权限：{acl}");
                }
                sb.AppendLine($"- 活动会话 {shares.Sessions.Count} 个，已打开文件 {shares.OpenFiles} 个");
            }
        }
        catch { /* optional */ }

        try
        {
            var snaps = await service.GetSnapshotsAsync(session);
            if (snaps.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## 卷影副本");
                sb.AppendLine($"- 共 {snaps.Count} 个，最近 {Math.Min(snaps.Count, MaxSnapshots)} 个：");
                foreach (var s in snaps.Take(MaxSnapshots))
                    sb.AppendLine($"  - {s.VolumeName} @ {Format.Date(s.InstallDate)}");
            }
            else sb.AppendLine("\n## 卷影副本\n- 当前没有快照");
        }
        catch { /* optional */ }

        try
        {
            var backup = await service.GetBackupAsync(session);
            if (backup.Task is not null)
            {
                sb.AppendLine();
                sb.AppendLine("## 备份任务");
                sb.AppendLine($"- {backup.Task.Path}：{backup.Task.StateText}，" +
                              $"上次运行 {Format.Date(backup.Task.LastRun)}，结果 {backup.Task.ResultText}，" +
                              $"下次 {Format.Date(backup.Task.NextRun)}");
                sb.AppendLine($"- robocopy 正在运行：{(backup.RobocopyRunning ? "是" : "否")}");
            }
        }
        catch { /* optional */ }

        var policies = AppState.Settings.SnapshotPolicies;
        if (policies.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## 定时快照策略");
            foreach (var p in policies)
                sb.AppendLine($"- {p.Summary}（{(p.Enabled ? "启用" : "停用")}）");
        }

        return sb.ToString();
    }
}
