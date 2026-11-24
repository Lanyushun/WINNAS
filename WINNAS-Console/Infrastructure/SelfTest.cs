using System.Globalization;
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Media;
using Winnas.Console.Models;
using Winnas.Console.Services;

namespace Winnas.Console.Infrastructure;

/// <summary>
/// "WINNAS.Console --selftest" runs every collector against the saved
/// connection and writes the result to a log file. It exists because a GUI
/// cannot be verified from a script — this is how the build is proven to
/// actually read real values, and it doubles as the field diagnostic.
/// </summary>
public static class SelfTest
{
    public static async Task<int> RunAsync()
    {
        var log = new List<string>();
        var failures = 0;

        void L(string line) => log.Add(line);

        L("WINNAS 自检");
        L("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        L("机器: " + Environment.MachineName);
        L("以管理员运行: " + IsElevated());
        L("");

        // WinExe has no console of its own; latch onto the caller's so the
        // output is visible when this is run from a terminal.
        AttachConsole(AttachParentProcess);

        WinnasSession? session = null;
        try
        {
            var profile = AppState.Current();
            L($"连接目标: {profile.Name} / {profile.Target} / {profile.TransportText}");
            L("配置的备份任务: " + AppState.Settings.BackupTask);
            L("配置的源目录: " + AppState.Settings.BackupSource);
            L("配置的日志目录: " + AppState.Settings.BackupLogDir);
            L("");

            // Skin switching touches the app resources, not the remote target,
            // so it runs before the session is opened: a broken palette would
            // otherwise be blamed on the connection.
            await Each("皮肤", log, () => Failures(async () =>
            {
                var original = ThemeManager.CurrentKey;
                try
                {
                    foreach (var skin in ThemeManager.Skins)
                    {
                        ThemeManager.Apply(skin.Key);
                        await Task.Yield();

                        var applied = Application.Current?.TryFindResource("PageBgBrush") as SolidColorBrush;
                        if (applied is null)
                            throw new InvalidOperationException($"{skin.Name}：应用后取不到 PageBgBrush");

                        var expected = (skin.PageBrush as SolidColorBrush)?.Color;
                        if (expected is null || applied.Color != expected)
                            throw new InvalidOperationException(
                                $"{skin.Name}：页面底色实际是 {applied.Color}，色板写的是 {expected}");

                        L($"     {skin.Name} ({skin.Key}) 页面底色={applied.Color} 主色={(skin.AccentBrush as SolidColorBrush)?.Color}");
                    }
                }
                finally
                {
                    ThemeManager.Apply(original);
                    L($"     已恢复到 {ThemeManager.Current.Name}");
                }
            }), () => failures++);

            // Sync and clock: pure local logic, no connection needed, so it runs
            // before the session is opened and can never be skipped because the
            // target is down.
            await Each("同步", log, () => Failures(async () =>
            {
                var snapshot = SettingsSync.Capture();
                var body = snapshot.Settings;

                if (body.ContainsKey("aiApiKey"))
                    throw new InvalidOperationException("快照包含 aiApiKey，DPAPI 密文会泄漏且到别处也解不开");
                if (body.ContainsKey("lastPageTitle") || body.ContainsKey("firstRunDone") ||
                    body.ContainsKey("lastConnectionId"))
                    throw new InvalidOperationException("快照包含本机导航状态，指纹会永远对不上");
                if (body.ContainsKey("windowLeft") || body.ContainsKey("windowWidth"))
                    throw new InvalidOperationException("快照包含窗口几何");

                if (body["connections"] is System.Text.Json.Nodes.JsonArray connections)
                    foreach (var node in connections)
                        if (node is System.Text.Json.Nodes.JsonObject c && c.ContainsKey("pwd"))
                            throw new InvalidOperationException("快照包含连接密码");

                var round = SettingsSync.Parse(SettingsSync.Serialize(snapshot))
                            ?? throw new InvalidOperationException("快照序列化后无法解析回来");
                if (SettingsSync.Fingerprint(round) != SettingsSync.Fingerprint(snapshot))
                    throw new InvalidOperationException("序列化往返后内容指纹不一致");

                var sample = new DateTime(2026, 3, 5, 14, 7, 9);
                var dmtf = TimeSync.DmtfFor(sample);
                if (!dmtf.StartsWith("20260305140709", StringComparison.Ordinal))
                    throw new InvalidOperationException($"DMTF 构造错误：{dmtf}");

                var behind = new ClockState(true, DateTime.Now,
                    DateTime.Now.AddSeconds(-900), -900, "", "");
                if (behind.DriftText != "慢 15.0 分钟")
                    throw new InvalidOperationException($"偏差文本错误：{behind.DriftText}");

                L($"     脱敏✓ 往返指纹={SettingsSync.Fingerprint(snapshot)} DMTF={dmtf} 偏差文本✓");
                await Task.CompletedTask;
            }), () => failures++);

            // Version comparison is pure local logic: deliberately no network
            // here, so this case still runs when GitHub is unreachable.
            await Each("更新检查", log, () => Failures(async () =>
            {
                if (UpdateChecker.Compare("1.6.1", "v1.6.0") <= 0)
                    throw new InvalidOperationException("本地比远端新时应返回正数");
                if (UpdateChecker.Compare("1.5.0", "v1.6.0") >= 0)
                    throw new InvalidOperationException("远端比本地新时应返回负数");
                if (UpdateChecker.Compare("1.6.1", "1.6.1") != 0)
                    throw new InvalidOperationException("相同版本应返回 0");
                if (UpdateChecker.Compare("1.6.1", "release-2026") != int.MinValue)
                    throw new InvalidOperationException("无法解析的标签应返回 int.MinValue");
                if (!UpdateChecker.RepoUrl.StartsWith("https://github.com/", StringComparison.Ordinal))
                    throw new InvalidOperationException("仓库地址异常：" + UpdateChecker.RepoUrl);
                if (UpdateChecker.ContactEmail != "3457754861@qq.com")
                    throw new InvalidOperationException("联系方式异常");

                L($"     版本比较✓ 仓库={UpdateChecker.Repo} 联系={UpdateChecker.ContactEmail}");
                await Task.CompletedTask;
            }), () => failures++);

            session = WinnasSession.Open(profile);
            L("[OK] CIM 会话已建立");

            var service = new WinnasService();
            await Each("概览", log, () => Failures(async () =>
            {
                var info = await service.GetOverviewAsync(session);
                L($"     主机={info.HostName} 系统={info.OsCaption}");
                L($"     CPU={info.CpuName} 负载={info.CpuLoad}% 核={info.Cores} 线程={info.Threads}");
                L($"     内存总量={Format.Bytes(info.MemTotal)} 可用={Format.Bytes(info.MemFree)} 运行时长={Format.Uptime(info.Uptime)}");
                foreach (var v in info.Volumes)
                    L($"     卷 {v.Drive} {v.Label} {v.UsageText} ({(int)(v.UsedRatio * 100)}%)");
                foreach (var d in info.Disks)
                    L($"     磁盘 {d.DeviceId} {d.Model} {Format.Bytes(d.Size)} {d.MediaText} {d.HealthText}");
                L($"     共享数={info.ShareCount}");
            }), () => failures++);

            await Each("存储", log, () => Failures(async () =>
            {
                var disks = await service.GetDisksAsync(session);
                foreach (var d in disks)
                {
                    L($"     {d.DeviceId} {d.Model} 介质={d.MediaText} 总线={d.BusText} 容量={Format.Bytes(d.Size)}");
                    L($"        健康={d.HealthText} 温度={Format.Temp(d.Temperature)} 通电={Format.Hours(d.PowerOnHours)} 磨损={Format.Count(d.Wear)} 读错={Format.Count(d.ReadErrorsTotal)} 写错={Format.Count(d.WriteErrorsTotal)}");
                    L($"        分区={string.Join(", ", d.Partitions.Select(p => p.DriveLetter + " " + Format.Bytes(p.Size)))}");
                }
            }), () => failures++);

            await Each("共享", log, () => Failures(async () =>
            {
                var snap = await service.GetSharesAsync(session);
                L($"     共享总数={snap.Shares.Count} 会话={snap.Sessions.Count} 打开文件={snap.OpenFiles}");
                foreach (var s in snap.Shares)
                {
                    var acl = string.Join(", ", s.Access.Select(a => $"{a.AccountName}/{a.RightText}"));
                    L($"     {s.Name} -> {s.Path} [{acl}] 连接数={s.CurrentUsers} 状态={s.StateText}");
                }
            }), () => failures++);

            await Each("快照", log, () => Failures(async () =>
            {
                var snaps = await service.GetSnapshotsAsync(session);
                L($"     卷影副本数={snaps.Count}");
                foreach (var s in snaps)
                    L($"     {s.VolumeName} 创建于 {Format.Date(s.InstallDate)} 可访问={s.ClientAccessible}");

                var vols = await service.GetVolumeLettersAsync(session);
                L($"     可选卷={string.Join(", ", vols)}");
            }), () => failures++);

            await Each("系统", log, () => Failures(async () =>
            {
                var info = await service.GetSystemAsync(session);
                foreach (var s in info.Services)
                    L($"     服务 {s.Name} ({s.DisplayName}) = {s.State} / {s.StartMode}");
                foreach (var a in info.Addresses)
                    L($"     IPv4 {a.Address}/{a.Prefix} on {a.Interface}");
                foreach (var r in info.Routes)
                    L($"     默认网关 {r.Gateway} via {r.Interface} metric {r.Metric}");
                foreach (var p in info.TopProcesses)
                    L($"     进程 {p.Name} ({p.Pid}) {Format.Bytes(p.Memory)}");
            }), () => failures++);

            await Each("备份", log, () => Failures(async () =>
            {
                var info = await service.GetBackupAsync(session);
                if (info.Task is null) L("     未找到配置的计划任务（请检查备份设置中的任务路径）");
                else
                {
                    L($"     任务={info.Task.Path} 状态={info.Task.StateText} 启用={info.Task.Enabled}");
                    L($"     上次运行={Format.Date(info.Task.LastRun)} 下次={Format.Date(info.Task.NextRun)} 结果={info.Task.ResultText}");
                }
                L($"     robocopy 正在运行={info.RobocopyRunning}");
                L($"     源目录统计={Format.Count(info.SourceFiles)} 个文件 / {Format.Bytes(info.SourceBytes)}");
                L($"     日志数={info.Logs.Count}");
                foreach (var f in info.Logs.Take(5))
                    L($"     {f.Name} {Format.Bytes(f.Size)} {Format.Date(f.LastWrite)}");
            }), () => failures++);

            await Each("备份计划引擎", log, () => Failures(async () =>
            {
                bool ok = true;
                void Assert(string label, bool cond)
                {
                    L($"     {(cond ? "[OK] " : "[FAIL] ")}{label}");
                    if (!cond) ok = false;
                }

                var mirror = new BackupPlan { Name = "自检-镜像", Source = "D:\\NAS", Target = "E:\\NAS-Mirror", Mode = 0 };
                var versioned = new BackupPlan { Name = "自检-版本", Source = "D:\\NAS", Target = "E:\\NAS-History", Mode = 2 };

                var mirrorArgs = BackupPlanner.BuildArgs(mirror, mirror.Source, mirror.Target, @"C:\logs\a.log");
                var dryArgs = BackupPlanner.BuildArgs(mirror, mirror.Source, mirror.Target, null, dryRun: true);
                var versionTarget = BackupPlanner.ResolveTarget(versioned, new DateTime(2026, 10, 23, 3, 0, 0));

                L("     镜像参数: " + mirrorArgs);
                L("     预演参数: " + dryArgs);
                L("     版本目标: " + versionTarget);

                Assert("镜像模式带 /MIR", mirrorArgs.Contains(" /MIR"));
                Assert("预演带 /L", dryArgs.Contains(" /L"));
                Assert("默认关闭进度条 /NP", mirrorArgs.Contains(" /NP"));
                Assert("排除目录进入 /XD", mirrorArgs.Contains("/XD"));
                Assert("日志用 /UNILOG", mirrorArgs.Contains("/UNILOG"));
                Assert("版本目录带时间戳", versionTarget.EndsWith("2026-10-23_0300"));

                var summary = BackupPlanner.ParseSummary(new[]
                {
                    "               总计      复制     跳过  不匹配      失败     其他",
                    "    文件：       345       340         5         0         0         0",
                    "    字节：   1.02 g    1.00 g      0.02 g         0         0         0",
                });
                L($"     摘要解析: 文件={summary.Files} 字节={Format.Bytes(summary.Bytes)} 失败={summary.Failed} 识别={summary.Found}");
                Assert("摘要被识别", summary.Found);
                Assert("复制文件数正确", summary.Files == 340);
                Assert("字节量约 1 GB", summary.Bytes >= 1024L * 1024 * 1024 && summary.Bytes <= 2L * 1024 * 1024 * 1024);

                var root = Path.Combine(Path.GetTempPath(), "winnas-rotate-" + Guid.NewGuid().ToString("N")[..6]);
                var todayDir = Path.Combine(root, DateTime.Now.ToString("yyyy-MM-dd_HHmm"));
                try
                {
                    Directory.CreateDirectory(todayDir);
                    Directory.CreateDirectory(Path.Combine(root, DateTime.Now.AddDays(-3).ToString("yyyy-MM-dd_HHmm")));
                    Directory.CreateDirectory(Path.Combine(root, DateTime.Now.AddDays(-12).ToString("yyyy-MM-dd_HHmm")));
                    Directory.CreateDirectory(Path.Combine(root, DateTime.Now.AddDays(-45).ToString("yyyy-MM-dd_HHmm")));

                    var rotatePlan = new BackupPlan { Mode = 2, Target = root, KeepDays = 1, KeepWeeks = 1, KeepMonths = 1 };

                    // Rotation is exercised against the temp folder on THIS
                    // machine, so it needs a local session. Handing it the
                    // app's current session made the test rewrite the path to
                    // \\NAS\C$\...\Temp and quietly clean nothing.
                    using var localSession = WinnasSession.Open(new ConnectionProfile
                    {
                        Name = "自检-本机",
                        Transport = TransportKind.Local,
                    });
                    var removed = await new BackupPlanner(localSession, service).RotateAsync(rotatePlan);
                    var left = Directory.GetDirectories(root).Length;
                    L($"     轮转: 清理 {removed} 份，剩余 {left} 份");
                    Assert("轮转清理了过期副本", removed >= 1);
                    Assert("最新一份被保留", Directory.Exists(todayDir));
                }
                finally
                {
                    try { Directory.Delete(root, true); } catch { /* temp dir is disposable */ }
                }

                var checks = await new BackupPlanner(session, service).PreflightAsync(mirror);
                foreach (var c in checks)
                    L($"     [{(c.Ok ? "通过" : c.Blocking ? "阻止" : "注意")}] {c.Name}：{c.Detail}");

                BackupRunStore.Add(new BackupRun
                {
                    PlanId = "selftest", PlanName = "自检", Ok = true, Finished = DateTime.Now,
                });
                var stored = BackupRunStore.For("selftest");
                L($"     历史存储: 写入后可读回 {stored.Count} 条");
                Assert("执行历史可写入并读回", stored.Count >= 1);
                BackupRunStore.Clear("selftest");

                return ok;
            }), () => failures++);

            await Each("文件", log, () => Failures(async () =>
            {
                var files = new FileService();
                var shares = await files.ListSharesAsync(session);
                L($"     可作为入口的共享={shares.Count}");
                foreach (var s in shares.Take(5)) L($"     {s.Name} -> {s.FullPath}");

                // With no shares to walk, fall back to the system drive so the
                // listing code is still exercised instead of silently skipped.
                var probe = shares.Count > 0 ? shares[0].FullPath : "C:\\";
                var items = await files.ListAsync(session, probe);
                L($"     列举 {probe} 得到 {items.Count} 条");
                foreach (var i in items.Take(6))
                    L($"     {(i.IsDirectory ? "[目录] " : "[文件] ")}{i.Name} {i.SizeText}");
            }), () => failures++);

            await Each("实时速率", log, () => Failures(async () =>
            {
                var sample = await service.GetPerfSampleAsync(session);
                L($"     CPU={sample.CpuLoad}%");
                L($"     磁盘 读={Format.Rate(sample.DiskRead)} 写={Format.Rate(sample.DiskWrite)}");
                L($"     网络 收={Format.Rate(sample.NetIn)} 发={Format.Rate(sample.NetOut)}");
                L($"     链路容量={Format.Bytes(sample.NetCapacity)}/s");
            }), () => failures++);

            await Each("远控", log, () => Failures(async () =>
            {
                var procs = await RemoteControlService.GetProcessesAsync(session);
                L($"     进程数={procs.Count} 内存前三={string.Join(", ", procs.Take(3).Select(p => p.Name + "/" + p.MemoryText))}");
                L($"     被标记为系统关键进程={procs.Count(p => p.Protected)} 个");

                var svcs = await RemoteControlService.GetServicesAsync(session);
                L($"     服务数={svcs.Count} 运行中={svcs.Count(x => x.Running)} 已停止={svcs.Count(x => x.Stopped)}");
                L($"     关键服务={string.Join(", ", svcs.Where(x => x.Critical).Select(x => x.Name + ":" + x.StateText))}");

                var mac = RemoteControlService.ParseMac("00-11-22-33-44-55");
                L($"     MAC 解析={BitConverter.ToString(mac ?? Array.Empty<byte>())}");

                var command = await RemoteControlService.RunCommandAsync(session, "hostname", 25);
                var flat = command.Output.Replace("\r", " ").Replace("\n", " ").Trim();
                L($"     远程命令 hostname → {(command.Ok ? "成功" : "失败")}，用时 {command.Seconds:0.0}s");
                L($"     被控端回复: {flat}");
                L($"     报文: {command.Message}");

                var ok = true;
                void Check(string label, bool cond)
                {
                    L($"     {(cond ? "[OK]" : "[FAIL]")} {label}");
                    if (!cond) ok = false;
                }

                Check("能列出被控端进程", procs.Count > 0);
                Check("System 进程被保护", procs.Any(p => p.Protected));
                Check("能列出被控端服务", svcs.Count > 0);
                Check("MAC 解析正确", mac is { Length: 6 } && mac[0] == 0x00 && mac[5] == 0x55);
                Check("非法 MAC 返回空", RemoteControlService.ParseMac("not-a-mac") is null);
                Check("远程命令带回输出", command.Ok && flat.Length > 0);
                return ok;
            }), () => failures++);

            await Each("凭据处理", log, () => Failures(async () =>
            {
                var p = new ConnectionProfile { Name = "t", Computer = "192.168.1.10", Transport = TransportKind.DCom };

                var (u1, d1) = NetworkLogon.Split(@"NASBOX\Administrator", p);
                var (u2, d2) = NetworkLogon.Split("Administrator", new ConnectionProfile { Domain = "CORP" });
                var (u3, d3) = NetworkLogon.Split("user@corp.local", p);
                L($"     显式域名 → {d1}\\{u1}");
                L($"     填了域字段 → {d2}\\{u2}");
                L($"     UPN 形式 → {d3}\\{u3}");

                // The old resolver chopped an IP at the first dot and produced "192".
                var ipDomain = NetworkLogon.MachineDomain("192.168.1.10");
                var nameDomain = NetworkLogon.MachineDomain("NASBOX");
                L($"     IP 的反向域名 → \"{ipDomain}\"（应为空或真实主机名）");
                L($"     主机名的域名 → \"{nameDomain}\"");

                L($"     本地账户显示 → {NetworkLogon.Describe("Administrator", p)}");

                var before = Environment.UserName;
                using (session.Impersonate()) { /* local session: must be a harmless no-op */ }
                var after = Environment.UserName;

                // Prove the native impersonation plumbing itself works without
                // needing a second account: impersonate our own token, then
                // make sure reverting restored the thread identity.
                string during;
                using (var self = System.Security.Principal.WindowsIdentity.GetCurrent())
                using (NetworkLogon.Impersonate(self.AccessToken))
                    during = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
                var restored = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
                L($"     自我模拟期间={during} 之后={restored}");

                var localReport = await ConnectionDiagnostics.RunAsync(new ConnectionProfile());
                L($"     本机诊断条目={localReport.Count}，结论={localReport[0].Detail}");

                var ok = true;
                void Check(string label, bool cond)
                {
                    L($"     {(cond ? "[OK]" : "[FAIL]")} {label}");
                    if (!cond) ok = false;
                }

                Check("主机名\\账户 拆分正确", u1 == "Administrator" && d1 == "NASBOX");
                Check("域字段生效", u2 == "Administrator" && d2 == "CORP");
                Check("UPN 拆分正确", u3 == "user" && d3 == "corp.local");
                Check("IP 不会被截成 192", !ipDomain.Contains('.') || IPAddress.TryParse(ipDomain, out _));
                Check("IP 不会被截成前一段", ipDomain != "192");
                Check("主机名原样作为域名", nameDomain == "NASBOX");
                Check("本地会话的模拟是空操作", before == after);
                Check("模拟后能正确还原身份", during == restored && restored.Length > 0);
                Check("本机诊断通过", localReport.Count >= 1 && localReport[0].Level == DiagLevel.Ok);
                return ok;
            }), () => failures++);

            await Each("快照策略", log, () => Failures(async () =>
            {
                var policies = AppState.Settings.SnapshotPolicies;
                L($"     策略数={policies.Count} 计划任务已注册={SnapshotPolicyService.IsTaskRegistered(session)}");
                foreach (var p in policies)
                    L($"     {p.Summary} 上次执行={Format.Date(p.LastRun)}");

                var due = policies.Count(p => SnapshotPolicyService.IsDue(p, DateTime.Now));
                L($"     当前到期待执行的策略数={due}");
            }), () => failures++);

            await Each("AI 上下文", log, () => Failures(async () =>
            {
                var brief = await NasContext.BuildAsync(session, service);
                L($"     摘要长度={brief.Length} 字符");
                L("     接口=" + AppState.Settings.AiBaseUrl);
                L("     模型=" + AppState.Settings.AiModel);
                L("     Key 已配置=" + (string.IsNullOrEmpty(AppState.Settings.AiApiKey) ? "否" : "是"));
            }), () => failures++);

            await Each("健康体检", log, () => Failures(async () =>
            {
                var report = await HealthCheck.InspectAsync(session, service);
                L($"     评分={report.Score} 结论={report.ScoreText} 严重={report.CriticalCount} 警告={report.WarnCount}");
                foreach (var f in report.Findings.Take(10))
                    L($"     [{f.SeverityText}] {f.Title}");
                if (report.Findings.Count == 0) L("     未发现问题");
            }), () => failures++);

            await Each("帮助主题", log, () => Failures(async () =>
            {
                await Task.CompletedTask;
                var groups = HelpTopics.All;
                L($"     分组数={groups.Count} 主题数={groups.Sum(g => g.Topics.Count)}");
                L($"     版本={HelpTopics.Version}");
                foreach (var g in groups) L($"     {g.Title}: {string.Join(" / ", g.Topics.Select(t => t.Title))}");

                var empty = groups.SelectMany(g => g.Topics)
                    .Count(t => string.IsNullOrWhiteSpace(t.Body));
                if (empty > 0) L($"     [注意] {empty} 条主题正文为空");
            }), () => failures++);

            await Each("操作日志", log, () => Failures(async () =>
            {
                ActivityLog.Info("自检", "自检写入测试");
                ActivityLog.Warn("自检", "自检警告测试", "带详情的一行");

                var raw = await Task.Run(() =>
                {
                    if (!Directory.Exists(ActivityLog.Dir)) return "<目录不存在>";
                    var files = new DirectoryInfo(ActivityLog.Dir).EnumerateFiles("activity-*.log").ToList();
                    return string.Join(" | ", files.Select(f =>
                    {
                        using var fs = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var rd = new StreamReader(fs);
                        var n = 0;
                        while (rd.ReadLine() is not null) n++;
                        return $"{f.Name}:{n}行";
                    }));
                });
                L($"     磁盘文件={raw}");

                var history = await Task.Run(() => ActivityLog.LoadHistory(days: 1));
                L($"     落盘后可读回 {history.Count} 条");
                L($"     日志目录={ActivityLog.Dir}");

                var newest = history.FirstOrDefault();
                L($"     最新一条={newest?.TimeText} [{newest?.LevelText}] {newest?.Category} {newest?.Message}");

                var csv = Exporter.ToCsv(new[] { "列1", "列2" },
                    new List<object?[]> { new object?[] { "中文, 带逗号", 123 } });
                L("     CSV 行数=" + csv.Trim().Split('\n').Length);
                L("     CSV 首行=" + csv.Split('\n')[0].Trim());

                if (history.Count == 0) { L("     [FAIL] 日志写盘后读不回来"); return false; }
                return true;
            }), () => failures++);
        }
        catch (Exception ex)
        {
            failures++;
            L("[FAIL] 无法建立会话: " + OpResult.From(ex).Message);
        }
        finally
        {
            session?.Dispose();
        }

        L("");
        L(failures == 0 ? "== 自检通过 ==" : $"== 自检完成，{failures} 项失败 ==");

        var text = string.Join(Environment.NewLine, log);
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WINNAS-Console", "selftest.log");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text, System.Text.Encoding.UTF8);
            System.Console.WriteLine(text);
            System.Console.WriteLine("\n日志: " + path);
        }
        catch { /* headless run: the log is a nice-to-have */ }

        FreeConsole();
        return failures == 0 ? 0 : 1;
    }

    private const int AttachParentProcess = -1;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    private static async Task Each(string label, List<string> log, Func<Task<bool>> body, Action onFail)
    {
        log.Add($"[{label}]");
        try
        {
            var ok = await body();
            log.Add(ok ? "     [OK]" : "     [FAIL]");
            if (!ok) onFail();
        }
        catch (Exception ex)
        {
            log.Add("     异常: " + OpResult.From(ex).Message);
            log.Add("     [FAIL]");
            onFail();
        }
        log.Add("");
    }

    private static async Task<bool> Failures(Func<Task> body)
    {
        await body();
        return true;
    }

    /// <summary>Variant for checks that can fail without throwing.</summary>
    private static async Task<bool> Failures(Func<Task<bool>> body) => await body();

    private static bool IsElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }
}
