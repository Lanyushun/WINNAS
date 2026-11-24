using System.IO;
using System.Text;
using Winnas.Console.Models;
using Winnas.Console.Services;

namespace Winnas.Console.Infrastructure;

/// <summary>
/// Headless run of one backup plan: "WINNAS.Console.exe --runplan &lt;id&gt;",
/// which is what Task Scheduler calls. Unlike the snapshot scheduler this one
/// returns non-zero on failure — a backup that did not happen is exactly the
/// thing the user needs to see in Task Scheduler's history.
/// </summary>
public static class BackupCli
{
    private const int MaxLogLines = 800;

    public static async Task<int> RunAsync(string planId)
    {
        var lines = new List<string>
        {
            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 备份计划触发：{planId}",
        };

        AgentDeployer.AdoptSidecarPlans();

        var plan = AppState.FindPlan(planId);
        if (plan is null)
        {
            lines.Add("  ! 找不到该计划（可能已被删除）");
            Write(lines);
            return 1;
        }

        lines.Add($"计划：{plan.Name} ｜ {plan.Source} → {plan.Target} ｜ {plan.ModeText}");

        if (!plan.Enabled)
        {
            lines.Add("  计划已停用，跳过");
            Write(lines);
            return 0;
        }

        var profile = AppState.Current();
        lines.Add($"目标：{profile.Name}（{profile.Target} · {profile.TransportText}）");

        try
        {
            using var session = WinnasSession.Open(profile);
            var planner = new BackupPlanner(session, new WinnasService());

            var checks = await planner.PreflightAsync(plan);
            var blocking = checks.Where(c => !c.Ok && c.Blocking).ToList();
            foreach (var c in blocking) lines.Add($"  ! {c.Name}：{c.Detail}");

            if (blocking.Count > 0)
            {
                lines.Add("  前置检查未通过，本次未执行");
                ActivityLog.Warn("备份", "计划被前置检查拦下", $"{plan.Name}：{string.Join("；", blocking.Select(b => b.Name))}");
                Write(lines);
                return 1;
            }

            var run = await planner.RunAsync(plan);
            lines.Add($"  {run.ResultText}：{run.Message}（耗时 {run.DurationText}）");
            lines.Add($"  日志：{run.LogPath}");

            if (run.Ok) ActivityLog.Ok("备份", "计划执行完成", $"{plan.Name}：{run.Files} 个文件 / {run.SizeText}");
            else ActivityLog.Error("备份", "计划执行失败", $"{plan.Name}：{run.Message}");

            Write(lines);
            return run.Ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            var msg = OpResult.From(ex).Message;
            lines.Add("  ! 执行失败：" + msg);
            ActivityLog.Error("备份", "计划执行异常", msg);
            Write(lines);
            return 1;
        }
    }

    private static void Write(List<string> lines)
    {
        try
        {
            Directory.CreateDirectory(AppState.Dir);
            var path = Path.Combine(AppState.Dir, "backup-schedule.log");

            var existing = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
            existing.AddRange(lines);
            if (existing.Count > MaxLogLines) existing = existing.Skip(existing.Count - MaxLogLines).ToList();

            File.WriteAllLines(path, existing);

            System.Console.OutputEncoding = Encoding.UTF8;
            System.Console.WriteLine(string.Join(Environment.NewLine, lines));
        }
        catch { /* headless by design */ }
    }
}
