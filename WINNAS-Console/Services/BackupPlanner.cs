using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Winnas.Console.Infrastructure;
using Winnas.Console.Models;

namespace Winnas.Console.Services;

/// <summary>
/// Turns a BackupPlan into an actual robocopy run — on this machine or on the
/// managed one — and into a scheduled task so it keeps happening when the
/// console is closed.
///
/// Two decisions shape the whole class:
///  1. Source and target are paths on the TARGET, so execution has to happen
///     there. Locally we start a process; remotely we ask Win32_Process.Create
///     to start one, because CIM exposes no other way to run a command.
///  2. robocopy's console output is written in the OEM code page, which .NET
///     cannot decode without the optional code-pages package. Everything
///     therefore goes through /UNILOG to a file we poll instead — one code
///     path for local and remote, no mojibake, and progress is visible while
///     the copy runs.
/// </summary>
public sealed class BackupPlanner
{
    private readonly WinnasSession _s;
    private readonly WinnasService _svc;

    public BackupPlanner(WinnasSession session, WinnasService service)
    {
        _s = session;
        _svc = service;
    }

    // ------------------------------------------------------------ paths

    /// <summary>Destination directory for this run; versioned mode appends a
    /// timestamp so each run leaves its own copy behind.</summary>
    public static string ResolveTarget(BackupPlan plan, DateTime now) =>
        plan.Mode == 2
            ? Path.Combine(plan.Target, now.ToString("yyyy-MM-dd_HHmm", CultureInfo.InvariantCulture))
            : plan.Target;

    /// <summary>Log path as the target machine sees it.</summary>
    public static string LocalLogPath(BackupPlan plan, DateTime now)
    {
        var dir = AppState.Settings.BackupPlanLogDir;
        if (string.IsNullOrWhiteSpace(dir)) dir = @"C:\ProgramData\WINNAS-Console\backup-logs";
        return Path.Combine(dir, $"{plan.Id}-{now:yyyyMMdd-HHmm}.log");
    }

    /// <summary>The same log, reachable from wherever the console runs.</summary>
    public string AccessibleLogPath(string localLogPath) => WinnasService.PathFor(_s, localLogPath);

    // --------------------------------------------------------- robocopy args

    /// <summary>
    /// Builds the robocopy argument string. Kept public and side-effect free so
    /// the self test can assert on it without touching a disk.
    /// </summary>
    public static string BuildArgs(BackupPlan plan, string source, string target,
        string? logPath = null, bool dryRun = false)
    {
        var sb = new StringBuilder();
        sb.Append('"').Append(source.TrimEnd('\\')).Append("\" \"").Append(target.TrimEnd('\\')).Append('"');

        sb.Append(plan.Mode switch
        {
            0 => " /MIR",   // mirror: includes /E and purges what left the source
            1 => " /E",     // accumulate: never deletes on the target
            _ => " /E",     // versioned: one timestamped directory per run
        });

        if (plan.CopySecurity) sb.Append(" /SEC");

        if (plan.Threads is > 1 and <= 128) sb.Append(" /MT:").Append(plan.Threads);
        sb.Append(" /R:").Append(Math.Clamp(plan.Retries, 0, 100));
        sb.Append(" /W:").Append(Math.Clamp(plan.WaitSeconds, 0, 60));
        if (plan.ThrottleMs > 0) sb.Append(" /IPG:").Append(plan.ThrottleMs);

        var dirs = SplitList(plan.ExcludeDirs);
        if (dirs.Count > 0) sb.Append(" /XD ").Append(string.Join(' ', dirs.Select(Quote)));

        var files = SplitList(plan.ExcludeFiles);
        if (files.Count > 0) sb.Append(" /XF ").Append(string.Join(' ', files));

        // No percent counter — /UNILOG files with \r progress lines are noise.
        sb.Append(" /NP /NDL");

        if (dryRun) sb.Append(" /L");

        var extra = (plan.ExtraArgs ?? "").Trim();
        if (extra.Length > 0) sb.Append(' ').Append(extra);

        // /UNILOG last: a trailing log switch is what robocopy expects and it
        // keeps the generated command readable when shown in the UI.
        if (!string.IsNullOrWhiteSpace(logPath)) sb.Append(" /UNILOG:\"").Append(logPath).Append('"');

        return sb.ToString();
    }

    private static List<string> SplitList(string? raw) =>
        (raw ?? "")
            .Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .ToList();

    private static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;

    /// <summary>Ready-to-paste command shown in the UI and used by the exported .bat.</summary>
    public string BuildCommand(BackupPlan plan, DateTime now) =>
        "robocopy " + BuildArgs(plan, plan.Source, ResolveTarget(plan, now), LocalLogPath(plan, now));

    // ------------------------------------------------------------ preflight

    /// <summary>
    /// The "should we even start" pass. This is what makes the planner react to
    /// the machine's actual state instead of blindly firing a copy.
    /// </summary>
    public async Task<List<PreflightCheck>> PreflightAsync(BackupPlan plan)
    {
        var checks = new List<PreflightCheck>();
        var now = DateTime.Now;
        var target = ResolveTarget(plan, now);

        await Task.Run(() =>
        {
            using var _ = _s.Impersonate();
            var srcAccess = WinnasService.PathFor(_s, plan.Source);
            var dstAccess = WinnasService.PathFor(_s, target);

            // 1. source reachable
            var srcOk = Safe(() => Directory.Exists(srcAccess));
            checks.Add(new PreflightCheck
            {
                Name = "源目录可达",
                Ok = srcOk,
                Detail = srcOk ? plan.Source : $"访问不到 {srcAccess}（远程时依赖管理员共享 盘符$）",
            });

            // 2. target or its parent exists
            bool dstOk = false, parentOk = false;
            try
            {
                dstOk = Directory.Exists(dstAccess);
                if (!dstOk)
                {
                    var parent = Path.GetDirectoryName(dstAccess.TrimEnd('\\'));
                    parentOk = parent is not null && Directory.Exists(parent);
                }
            }
            catch { /* share unreachable */ }

            checks.Add(new PreflightCheck
            {
                Name = "目标位置可用",
                Ok = dstOk || parentOk,
                Detail = dstOk ? target : parentOk ? $"{target} 将自动创建" : $"目标与其父目录都不存在：{target}",
            });

            // 3. free space on the target versus what the source holds
            var free = FreeBytesOn(_s, plan.Target);
            var total = TotalBytesOn(_s, plan.Target);
            var sourceBytes = EstimateSourceBytes(plan);

            if (free is null)
            {
                checks.Add(new PreflightCheck
                {
                    Name = "目标剩余空间",
                    Ok = false,
                    Blocking = false,
                    Detail = "读不到目标卷容量，按可用处理",
                });
            }
            else
            {
                // Copying inside one volume does not consume another full copy
                // of the data — only the delta does. Treating it as a full copy
                // would block every sane mirror on the same disk.
                var sameVolume = SameVolume(plan.Source, plan.Target);
                var need = sameVolume && plan.Mode != 2 ? 0 : sourceBytes;

                var after = free.Value - need;
                var afterRatio = total is > 0 ? (double)after / total.Value : 1;
                var minRatio = plan.MinFreePercent / 100.0;
                var ok = after > 0 && afterRatio >= minRatio;

                var basis = need == 0
                    ? "同卷复制，只占增量"
                    : $"按源卷已用空间估算 {Format.Bytes(need)}（最坏情况）";

                checks.Add(new PreflightCheck
                {
                    Name = "目标剩余空间",
                    Ok = ok,
                    Detail = ok
                        ? $"{basis}；剩余 {Format.Bytes(free.Value)}，复制后约剩 {(int)(afterRatio * 100)}%"
                        : $"{basis}；剩余只有 {Format.Bytes(free.Value)}，复制后剩 {(int)Math.Max(0, afterRatio * 100)}%（下限 {plan.MinFreePercent}%）",
                });
            }

            // 4. volume health
            var health = VolumeHealth(_s, plan.Target);
            checks.Add(new PreflightCheck
            {
                Name = "目标卷健康",
                Ok = health is null or 0,
                Blocking = false,
                Detail = health is null ? "未读到卷健康状态" : health == 0 ? "健康" : $"卷状态异常（{health}）",
            });

            // 5. is a copy already running?
            var running = Safe(() => _s.Query(Cim.CimV2,
                "SELECT ProcessId FROM Win32_Process WHERE Name='robocopy.exe'").Count) > 0;
            checks.Add(new PreflightCheck
            {
                Name = "没有其他备份在跑",
                Ok = !running,
                Blocking = plan.SkipIfRunning,
                Detail = running
                    ? (plan.SkipIfRunning ? "已有 robocopy 在运行，按设置跳过本次" : "已有 robocopy 在运行（设置允许继续）")
                    : "当前没有 robocopy 进程",
            });

            // 6. on battery?
            var onBattery = Safe(() => _s.Query(Cim.CimV2,
                "SELECT BatteryStatus FROM Win32_Battery").Any(b => Cim.Int(b, "BatteryStatus") == 1));
            if (onBattery)
            {
                checks.Add(new PreflightCheck
                {
                    Name = "电源状态",
                    Ok = false,
                    Blocking = plan.SkipOnBattery,
                    Detail = plan.SkipOnBattery ? "正在使用电池，按设置跳过" : "正在使用电池（设置允许继续）",
                });
            }

            // 7. log directory writable — without it a remote run is blind
            var logDir = Path.GetDirectoryName(LocalLogPath(plan, now));
            var logOk = false;
            try
            {
                var access = WinnasService.PathFor(_s, logDir ?? "");
                Directory.CreateDirectory(access);
                logOk = Directory.Exists(access);
            }
            catch { /* not an admin on the target */ }

            checks.Add(new PreflightCheck
            {
                Name = "日志目录可写",
                Ok = logOk,
                Blocking = false,
                Detail = logOk
                    ? logDir ?? ""
                    : $"无法创建 {logDir}，远程执行将拿不到输出（需要目标机管理员权限）",
            });
        });

        return checks;
    }

    private static bool SameVolume(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return false;
        return char.ToUpperInvariant(a[0]) == char.ToUpperInvariant(b[0]) && a[0] != '/' && a[0] != '\\';
    }

    private long EstimateSourceBytes(BackupPlan plan)
    {
        // A cached measurement is exact enough; without one, fall back to how
        // much of the source volume is in use so the check is never silent.
        if (_s.Cache.TryGet<SourceStat>("backupSource", TimeSpan.FromHours(6), out var stat)
            && stat is not null && stat.Bytes > 0)
            return stat.Bytes;

        var letter = plan.Source.Length >= 1 ? plan.Source[..1] : "";
        if (letter.Length == 0) return 0;

        var vol = Safe(() => _s.Query(Cim.StorageNs, "SELECT * FROM MSFT_Volume")
            .FirstOrDefault(v => string.Equals(Cim.DriveLetter(v, "DriveLetter"), letter,
                StringComparison.OrdinalIgnoreCase)));

        return vol is null ? 0 : Math.Max(0, Cim.Long(vol, "Size") - Cim.Long(vol, "SizeRemaining"));
    }

    private static long? FreeBytesOn(WinnasSession s, string path) => VolumeOf(s, path, free: true);
    private static long? TotalBytesOn(WinnasSession s, string path) => VolumeOf(s, path, free: false);

    private static long? VolumeOf(WinnasSession s, string path, bool free)
    {
        var letter = path.Length >= 1 ? path[..1] : "";
        if (letter.Length == 0) return null;

        var vol = Safe(() => s.Query(Cim.StorageNs, "SELECT * FROM MSFT_Volume")
            .FirstOrDefault(v => string.Equals(Cim.DriveLetter(v, "DriveLetter"), letter,
                StringComparison.OrdinalIgnoreCase)));

        if (vol is null) return null;
        return free ? Cim.Long(vol, "SizeRemaining") : Cim.Long(vol, "Size");
    }

    private static int? VolumeHealth(WinnasSession s, string path)
    {
        var letter = path.Length >= 1 ? path[..1] : "";
        if (letter.Length == 0) return null;

        var vol = Safe(() => s.Query(Cim.StorageNs, "SELECT * FROM MSFT_Volume")
            .FirstOrDefault(v => string.Equals(Cim.DriveLetter(v, "DriveLetter"), letter,
                StringComparison.OrdinalIgnoreCase)));

        return vol is null ? null : Cim.Int(vol, "HealthStatus", 0);
    }

    private static T Safe<T>(Func<T> body)
    {
        try { return body(); } catch { return default!; }
    }

    // ------------------------------------------------------------- runs

    /// <summary>robocopy /L: list what would happen, change nothing.</summary>
    public Task<BackupRun> DryRunAsync(BackupPlan plan, Action<string>? onLine = null,
        CancellationToken ct = default) =>
        ExecuteAsync(plan, dryRun: true, onLine, ct);

    public Task<BackupRun> RunAsync(BackupPlan plan, Action<string>? onLine = null,
        CancellationToken ct = default) =>
        ExecuteAsync(plan, dryRun: false, onLine, ct);

    private async Task<BackupRun> ExecuteAsync(BackupPlan plan, bool dryRun,
        Action<string>? onLine, CancellationToken ct)
    {
        var now = DateTime.Now;
        var target = ResolveTarget(plan, now);
        var logFile = LocalLogPath(plan, now);
        var args = BuildArgs(plan, plan.Source, target, logFile, dryRun);
        var run = new BackupRun
        {
            PlanId = plan.Id,
            PlanName = plan.Name,
            Started = now,
            DryRun = dryRun,
            LogPath = logFile,
        };

        var textTag = dryRun ? "预演" : "执行";
        onLine?.Invoke($"[{textTag}] robocopy {args}");

        if (plan.SnapshotFirst && !dryRun)
        {
            var snap = await _svc.CreateSnapshotAsync(_s, plan.Source.Length >= 2 ? plan.Source[..2] : plan.Source);
            var line = snap.Ok
                ? "[快照] 已为源卷创建卷影副本，误删后可回滚"
                : $"[快照] 创建失败（继续执行）：{snap.Message}";
            onLine?.Invoke(line);
        }

        var deadline = DateTime.Now.AddMinutes(Math.Clamp(plan.MaxMinutes, 1, 1440));
        int exitCode;

        if (_s.IsRemote) exitCode = await RunRemoteAsync(args, logFile, deadline, onLine, ct);
        else exitCode = await RunLocalAsync(args, logFile, deadline, onLine, ct);

        run.Finished = DateTime.Now;
        run.ExitCode = exitCode;

        var summary = ReadSummary(logFile);
        run.Files = summary.Files;
        run.Bytes = summary.Bytes;
        run.Failed = summary.Failed;

        // Remote runs cannot report an exit code, so the closing summary is the
        // only honest signal: a copy that ended without one did not finish.
        run.Ok = _s.IsRemote
            ? summary.Found && summary.Failed == 0
            : exitCode < 8;

        run.Message = run.Ok
            ? (dryRun ? "预演完成" : "备份完成") +
              $"：{summary.Files} 个文件 / {summary.SizeText}"
            : (_s.IsRemote
                ? summary.Found
                    ? $"复制失败 {summary.Failed} 项，详见日志"
                    : "未生成 robocopy 摘要，可能被中断或超时"
                : $"robocopy 退出码 {exitCode}（≥8 视为失败）");

        onLine?.Invoke($"[{textTag}] {run.Message}");

        if (!dryRun)
        {
            plan.LastRun = run.Finished;
            plan.LastExitCode = exitCode;
            if (run.Ok)
            {
                plan.LastSuccess = run.Finished;
                plan.LastFiles = run.Files;
                plan.LastBytes = run.Bytes;
                plan.LastError = null;
            }
            else
            {
                plan.LastError = run.Message;
            }
            AppState.Save();

            var rotated = await RotateAsync(plan);
            if (rotated > 0) onLine?.Invoke($"[轮转] 清理了 {rotated} 份超出保留策略的旧副本");
        }

        BackupRunStore.Add(run);
        return run;
    }

    private async Task<int> RunLocalAsync(string args, string logFile, DateTime deadline,
        Action<string>? onLine, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("robocopy.exe", args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var p = Process.Start(psi);
        if (p is null) return 16;

        var tailer = new LogTailer(logFile, onLine);
        tailer.Start();

        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { /* already gone */ }
            tailer.Stop();
            return 16;
        }

        // Give the last buffered lines a moment to land, then stop tailing.
        await Task.Delay(300, CancellationToken.None);
        tailer.Stop();
        tailer.Flush();

        return p.ExitCode;
    }

    private async Task<int> RunRemoteAsync(string args, string logFile, DateTime deadline,
        Action<string>? onLine, CancellationToken ct)
    {
        var command = "robocopy.exe " + args;

        var result = Safe(() => _s.InvokeStatic(Cim.CimV2, "Win32_Process", "Create",
            new Dictionary<string, object> { ["CommandLine"] = command }));

        if (result is null)
        {
            onLine?.Invoke("[错误] 无法在目标机上创建进程（需要管理员权限，且远程 DCOM/WinRM 可用）");
            return 16;
        }

        var rc = result.ReturnCode();
        if (rc != 0)
        {
            onLine?.Invoke($"[错误] 目标机拒绝创建进程，返回 0x{rc:X8}。远程执行需要管理员权限。");
            return 16;
        }

        uint pid = 0;
        try { pid = Convert.ToUInt32(result.Properties["ProcessId"]?.Value ?? 0); }
        catch { /* some targets omit it */ }

        var tailer = new LogTailer(WinnasService.PathFor(_s, logFile), onLine);
        tailer.Start();

        try
        {
            while (DateTime.Now < deadline)
            {
                await Task.Delay(1500, ct);
                if (pid == 0) break;

                var alive = Safe(() => _s.Query(Cim.CimV2,
                    $"SELECT ProcessId FROM Win32_Process WHERE ProcessId={pid}").Count) > 0;
                if (!alive) break;
            }
        }
        catch (OperationCanceledException)
        {
            onLine?.Invoke("[取消] 已停止等待，目标机上的 robocopy 仍在后台运行");
        }

        await Task.Delay(500, CancellationToken.None);
        tailer.Stop();
        tailer.Flush();

        // No exit code survives a remote process; the summary decides.
        return 0;
    }

    /// <summary>Reads robocopy's closing table out of a /UNILOG file.</summary>
    public RobocopySummary ReadSummary(string localLogPath)
    {
        try
        {
            using var _ = _s.Impersonate();
            var path = WinnasService.PathFor(_s, localLogPath);
            if (!File.Exists(path)) return new RobocopySummary();

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var rd = new StreamReader(fs, Encoding.Unicode, detectEncodingFromByteOrderMarks: true);

            var lines = new List<string>();
            while (rd.ReadLine() is { } line) lines.Add(line);
            return ParseSummary(lines);
        }
        catch { return new RobocopySummary(); }
    }

    /// <summary>Pure parsing, so the self test can feed it known text.</summary>
    public static RobocopySummary ParseSummary(IEnumerable<string> lines)
    {
        var summary = new RobocopySummary();

        foreach (var raw in lines)
        {
            var line = raw.Replace('\uFEFF', ' ');

            // Chinese robocopy writes "文件:" with an ASCII colon, not the
            // full-width one, so match either.
            if (!summary.Found && Regex.IsMatch(line, @"(文件|Files)\s*[:：]"))
            {
                var numbers = ParseTokens(line).ToList();
                if (numbers.Count >= 2)
                {
                    summary.Files = (long)numbers[1];
                    if (numbers.Count >= 5) summary.Failed = (long)numbers[4];
                    summary.Found = true;
                }
            }

            if (Regex.IsMatch(line, @"(字节|Bytes)\s*[:：]"))
            {
                var numbers = ParseTokens(line).ToList();
                if (numbers.Count >= 2) summary.Bytes = (long)numbers[1];
            }
        }

        return summary;
    }

    /// <summary>Handles both plain counts and robocopy's "1.02 g" byte tokens.</summary>
    private static IEnumerable<double> ParseTokens(string line)
    {
        foreach (Match m in Regex.Matches(line, @"(\d+(?:[.,]\d+)?)\s*([kKmMgGtT])?(?![kKmMgGtT]*\d)"))
        {
            var value = double.Parse(m.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
            var unit = m.Groups[2].Value.ToLowerInvariant();
            yield return unit switch
            {
                "k" => value * 1024,
                "m" => value * 1024 * 1024,
                "g" => value * 1024.0 * 1024 * 1024,
                "t" => value * 1024.0 * 1024 * 1024 * 1024,
                _ => value,
            };
        }
    }

    // ---------------------------------------------------------- retention

    /// <summary>GFS rotation for versioned plans: newest copy per day/week/month
    /// survives, everything older than the window is deleted.</summary>
    public async Task<int> RotateAsync(BackupPlan plan)
    {
        if (plan.Mode != 2) return 0;

        return await Task.Run(() =>
        {
            using var _ = _s.Impersonate();
            var root = WinnasService.PathFor(_s, plan.Target);
            if (!Directory.Exists(root)) return 0;

            var stamped = new List<(string Path, DateTime Time)>();
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(dir);
                if (DateTime.TryParseExact(name, "yyyy-MM-dd_HHmm", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var when))
                    stamped.Add((dir, when));
            }

            if (stamped.Count == 0) return 0;

            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var g in stamped.GroupBy(x => x.Time.Date).OrderByDescending(g => g.Key).Take(plan.KeepDays))
                keep.Add(g.OrderByDescending(x => x.Time).First().Path);

            foreach (var g in stamped
                         .GroupBy(x => ISOWeek.GetYear(x.Time) * 100 + ISOWeek.GetWeekOfYear(x.Time))
                         .OrderByDescending(g => g.Key).Take(plan.KeepWeeks))
                keep.Add(g.OrderByDescending(x => x.Time).First().Path);

            foreach (var g in stamped
                         .GroupBy(x => x.Time.Year * 100 + x.Time.Month)
                         .OrderByDescending(g => g.Key).Take(plan.KeepMonths))
                keep.Add(g.OrderByDescending(x => x.Time).First().Path);

            var removed = 0;
            foreach (var item in stamped.Where(x => !keep.Contains(x.Path)))
            {
                try
                {
                    Directory.Delete(item.Path, recursive: true);
                    removed++;
                }
                catch { /* locked or permission: leave it for the next pass */ }
            }
            return removed;
        });
    }

    // --------------------------------------------------------- scheduling

    public static string TaskNameFor(BackupPlan plan) =>
        "WINNAS Backup - " + (string.IsNullOrWhiteSpace(plan.Name) ? plan.Id : plan.Name).Replace("\\", "-");

    public bool IsRegistered(BackupPlan plan)
    {
        var name = TaskNameFor(plan);
        var args = $"/Query /TN \"{name}\"";
        var (code, _, _) = RunSchtasks(args);
        return code == 0;
    }

    public async Task<OpResult> RegisterAsync(BackupPlan plan)
    {
        var name = TaskNameFor(plan);
        var schedule = plan.Frequency switch
        {
            0 => "/SC DAILY /MO 1",
            1 => $"/SC WEEKLY /D {WeekToken(plan.DayOfWeek)}",
            2 => $"/SC MONTHLY /D {Math.Clamp(plan.DayOfMonth, 1, 31)}",
            _ => "",
        };

        if (schedule.Length == 0) return OpResult.Fail("该计划设为「仅手动」，无需注册定时任务");

        // The task runs on the managed machine, so both the EXE and the plan
        // definition have to exist there. A desktop path would fail silently
        // every night, which is the worst possible outcome for a backup.
        var (deploy, exe) = await AgentDeployer.EnsureAsync(_s);
        if (!deploy.Ok) return deploy;

        if (_s.IsRemote)
        {
            var sync = await AgentDeployer.WritePlansAsync(_s, AppState.Settings.BackupPlans);
            if (!sync.Ok) return sync;
        }

        var args = $"/Create /TN \"{name}\" /TR \"\\\"{exe}\\\" --runplan {plan.Id}\" " +
                   $"{schedule} /ST {plan.Hour:00}:{plan.Minute:00} /RL HIGHEST /F";

        var (code, stdout, stderr) = Schtasks.Run(_s, args, 25000);
        if (code != 0)
        {
            var hint = _s.IsRemote
                ? "远程注册需要目标机放行「远程计划任务管理」并具备管理员权限，可改用「导出脚本」在目标机上手动部署。"
                : "";
            return OpResult.Fail($"注册失败（{code}）：{FirstLine(stderr, stdout)}{hint}");
        }

        plan.Scheduled = true;
        AppState.Save();
        return OpResult.Good($"已在{(_s.IsRemote ? "被控端 " + _s.Profile.Target : "本机")}注册「{name}」，{plan.FrequencyText} 自动执行");
    }

    public OpResult Unregister(BackupPlan plan)
    {
        var name = TaskNameFor(plan);
        var (code, stdout, stderr) = RunSchtasks($"/Delete /TN \"{name}\" /F");
        if (code != 0) return OpResult.Fail($"删除失败（{code}）：{FirstLine(stderr, stdout)}");

        plan.Scheduled = false;
        AppState.Save();
        return OpResult.Good($"已删除定时任务「{name}」");
    }

    private static string WeekToken(int dayOfWeek) => dayOfWeek switch
    {
        0 => "SUN", 1 => "MON", 2 => "TUE", 3 => "WED", 4 => "THU", 5 => "FRI", 6 => "SAT",
        _ => "MON",
    };

    private (int Code, string Out, string Err) RunSchtasks(string args) => Schtasks.Run(_s, args);

    private static string FirstLine(params string[] sources)
    {
        foreach (var s in sources)
        {
            if (string.IsNullOrWhiteSpace(s)) continue;
            foreach (var line in s.Split('\n'))
            {
                var t = line.Trim();
                if (t.Length > 0) return t;
            }
        }
        return "无输出";
    }

    // -------------------------------------------------------- export

    /// <summary>A .bat that does the same thing without the console — handy when
    /// the target refuses remote task registration.</summary>
    public string BuildScript(BackupPlan plan)
    {
        var now = DateTime.Now;
        var target = ResolveTarget(plan, now);
        var log = LocalLogPath(plan, now);
        var args = BuildArgs(plan, plan.Source, target, log);

        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("rem WINNAS 备份计划：" + plan.Name);
        sb.AppendLine("rem 由 WINNAS 控制台导出，可直接在目标机上以管理员身份运行或注册为计划任务");
        sb.AppendLine();
        if (plan.SnapshotFirst)
        {
            var vol = plan.Source.Length >= 2 ? plan.Source[..2] : plan.Source;
            sb.AppendLine($"rem 先建卷影副本，误删时可回滚：vssadmin create shadow /For={vol}");
        }
        sb.AppendLine($"if not exist \"{Path.GetDirectoryName(log)}\" mkdir \"{Path.GetDirectoryName(log)}\"");
        sb.AppendLine();
        sb.AppendLine("robocopy " + args);
        sb.AppendLine("set RC=%ERRORLEVEL%");
        sb.AppendLine("rem robocopy 退出码小于 8 都表示成功（0-7 代表复制了文件）");
        sb.AppendLine("if %RC% LSS 8 (echo 备份完成 & exit /b 0)");
        sb.AppendLine("echo 备份失败，退出码 %RC%");
        sb.AppendLine("exit /b %RC%");
        return sb.ToString();
    }
}
