using System.IO;
using System.Management;
using Winnas.Console.Infrastructure;
using Winnas.Console.Models;

namespace Winnas.Console.Services;

/// <summary>
/// All data access for WINNAS. Everything here talks CIM (plus Task Scheduler
/// COM for scheduled-task details), and every method works against either the
/// local machine or a remote one — the caller passes the session, not a path.
/// </summary>
public sealed record SourceStat(long Files, long Bytes);

public sealed class WinnasService
{
    private static readonly TimeSpan OverviewTtl = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DisksTtl = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SharesTtl = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SnapshotsTtl = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SystemTtl = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan BackupTtl = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan SourceTtl = TimeSpan.FromMinutes(5);

    // ------------------------------------------------------------- overview

    public Task<OverviewInfo> GetOverviewAsync(WinnasSession s) => Task.Run(() =>
    {
        if (s.Cache.TryGet<OverviewInfo>("overview", OverviewTtl, out var cached) && cached is not null) return cached;

        var cs = s.First(Cim.CimV2, "SELECT * FROM Win32_ComputerSystem");
        var os = s.First(Cim.CimV2, "SELECT * FROM Win32_OperatingSystem");
        var cpu = s.First(Cim.CimV2, "SELECT * FROM Win32_Processor");

        var volumes = s.Query(Cim.StorageNs, "SELECT * FROM MSFT_Volume")
            .Where(v => Cim.DriveLetter(v, "DriveLetter").Length > 0)
            .Select(v => new VolumeBrief
            {
                Drive = Cim.DriveLetter(v, "DriveLetter") + ":",
                Label = Cim.Str(v, "FileSystemLabel"),
                FileSystem = Cim.Str(v, "FileSystem"),
                Size = Cim.Long(v, "Size"),
                Free = Cim.Long(v, "SizeRemaining"),
                Health = Cim.Int(v, "HealthStatus"),
            })
            .OrderBy(v => v.Drive)
            .ToList();

        var disks = s.Query(Cim.StorageNs, "SELECT * FROM MSFT_PhysicalDisk")
            .Select(d => new DiskBrief
            {
                DeviceId = Cim.Str(d, "DeviceId"),
                Model = Cim.Str(d, "FriendlyName"),
                Size = Cim.Long(d, "Size"),
                MediaText = MediaText(Cim.Int(d, "MediaType")),
                Health = Cim.Int(d, "HealthStatus"),
            })
            .ToList();

        var shares = s.Query(Cim.SmbNs, "SELECT * FROM MSFT_SmbShare");
        var shareCount = shares.Count(x => !Cim.Bool(x, "Special"));

        DateTime? boot = os is null ? null : Cim.Date(os, "LastBootUpTime");

        var info = new OverviewInfo
        {
            HostName = cs is null ? s.Profile.Target : Cim.Str(cs, "DNSHostName", Cim.Str(cs, "Name", s.Profile.Target)),
            OsCaption = os is null ? "" : Cim.Str(os, "Caption"),
            OsVersion = os is null ? "" : Cim.Str(os, "Version"),
            CpuName = cpu is null ? "" : Cim.Str(cpu, "Name").Trim(),
            CpuLoad = cpu is null ? 0 : Cim.Int(cpu, "LoadPercentage"),
            Cores = cpu is null ? 0 : Cim.Int(cpu, "NumberOfCores"),
            Threads = cpu is null ? 0 : Cim.Int(cpu, "NumberOfLogicalProcessors"),
            MemTotal = cs is null ? 0 : Cim.Long(cs, "TotalPhysicalMemory"),
            MemFree = os is null ? 0 : Cim.Long(os, "FreePhysicalMemory") * 1024L,
            BootTime = boot,
            Uptime = boot is null ? TimeSpan.Zero : DateTime.Now - boot.Value,
            Volumes = volumes,
            Disks = disks,
            ShareCount = shareCount,
        };

        s.Cache.Set("overview", info);
        return info;
    });

    private static string MediaText(int mediaType) => mediaType switch
    {
        3 => "HDD",
        4 => "SSD",
        5 => "SCM",
        _ => "未指定",
    };

    // --------------------------------------------------------------- disks

    public Task<List<DiskInfo>> GetDisksAsync(WinnasSession s) => Task.Run(() =>
    {
        if (s.Cache.TryGet<List<DiskInfo>>("disks", DisksTtl, out var cached) && cached is not null) return cached;

        var partitions = s.Query(Cim.StorageNs, "SELECT * FROM MSFT_Partition")
            .Where(p => Cim.DriveLetter(p, "DriveLetter").Length > 0)
            .Select(p => new PartitionBrief
            {
                DiskNumber = Cim.Int(p, "DiskNumber"),
                DriveLetter = Cim.DriveLetter(p, "DriveLetter") + ":",
                Size = Cim.Long(p, "Size"),
            })
            .ToList();

        var list = s.Query(Cim.StorageNs, "SELECT * FROM MSFT_PhysicalDisk").Select(d =>
        {
            var counter = s.Associated(Cim.StorageNs, d,
                "MSFT_PhysicalDiskToStorageReliabilityCounter", "MSFT_StorageReliabilityCounter").FirstOrDefault();

            var deviceIdText = Cim.Str(d, "DeviceId");
            int.TryParse(deviceIdText, out var deviceId);

            return new DiskInfo
            {
                DeviceId = deviceIdText,
                Model = Cim.Str(d, "FriendlyName"),
                Serial = Cim.Str(d, "SerialNumber").Trim(),
                Firmware = Cim.Str(d, "FirmwareVersion").Trim(),
                Size = Cim.Long(d, "Size"),
                MediaType = Cim.Int(d, "MediaType"),
                BusType = Cim.Int(d, "BusType"),
                Health = Cim.Int(d, "HealthStatus"),
                Usage = UsageText(Cim.Int(d, "Usage")),
                Temperature = NullableInt(counter, "Temperature"),
                TemperatureMax = NullableInt(counter, "TemperatureMax"),
                PowerOnHours = NullableLong(counter, "PowerOnHours"),
                PowerCycleCount = NullableLong(counter, "StartStopCycleCount"),
                ReadErrorsTotal = NullableLong(counter, "ReadErrorsTotal"),
                WriteErrorsTotal = NullableLong(counter, "WriteErrorsTotal"),
                Wear = NullableLong(counter, "Wear"),
                Partitions = partitions.Where(p => p.DiskNumber == deviceId).ToList(),
            };
        }).ToList();

        s.Cache.Set("disks", list);
        return list;
    });

    private static int? NullableInt(ManagementBaseObject? i, string name)
    {
        if (i is null) return null;
        var p = i.Properties[name];
        if (p?.Value is null) return null;
        try { return Convert.ToInt32(p.Value); }
        catch { return null; }
    }

    private static long? NullableLong(ManagementBaseObject? i, string name)
    {
        if (i is null) return null;
        var p = i.Properties[name];
        if (p?.Value is null) return null;
        try
        {
            var v = Convert.ToInt64(p.Value);
            return v == 0 ? null : v;
        }
        catch { return null; }
    }

    private static string UsageText(int usage) => usage switch
    {
        1 => "数据存储",
        2 => "系统保留",
        3 => "已停用",
        _ => "未知",
    };

    // ------------------------------------------------------ live metrics

    /// <summary>
    /// One sample of the live rates. PerfFormattedData already reports
    /// per-second values, so no delta between two frames is needed (verified
    /// by probe: the value moves on its own between samples).
    /// </summary>
    public Task<PerfSample> GetPerfSampleAsync(WinnasSession s) => Task.Run(() =>
    {
        long diskRead = 0, diskWrite = 0;
        foreach (var d in s.Query(Cim.CimV2,
                     "SELECT * FROM Win32_PerfFormattedData_PerfDisk_PhysicalDisk"))
        {
            // Summing every instance double counts _Total, so prefer it.
            if (string.Equals(Cim.Str(d, "Name"), "_Total", StringComparison.OrdinalIgnoreCase))
            {
                diskRead = Cim.Long(d, "DiskReadBytesPerSec");
                diskWrite = Cim.Long(d, "DiskWriteBytesPerSec");
                break;
            }
        }

        long netIn = 0, netOut = 0, bandwidth = 0;
        foreach (var n in s.Query(Cim.CimV2,
                     "SELECT * FROM Win32_PerfFormattedData_Tcpip_NetworkInterface"))
        {
            netIn += Cim.Long(n, "BytesReceivedPerSec");
            netOut += Cim.Long(n, "BytesSentPerSec");
            bandwidth += Cim.Long(n, "CurrentBandwidth") / 8; // bits -> bytes
        }

        int cpu = 0;
        foreach (var c in s.Query(Cim.CimV2,
                     "SELECT * FROM Win32_PerfFormattedData_PerfOS_Processor"))
        {
            if (string.Equals(Cim.Str(c, "Name"), "_Total", StringComparison.OrdinalIgnoreCase))
            {
                cpu = Cim.Int(c, "PercentProcessorTime");
                break;
            }
        }

        return new PerfSample
        {
            Time = DateTime.Now,
            DiskRead = diskRead,
            DiskWrite = diskWrite,
            NetIn = netIn,
            NetOut = netOut,
            NetCapacity = bandwidth,
            CpuLoad = cpu,
        };
    });

    // -------------------------------------------------------------- shares

    public Task<SharesSnapshot> GetSharesAsync(WinnasSession s) => Task.Run(() =>
    {
        if (s.Cache.TryGet<SharesSnapshot>("shares", SharesTtl, out var cached) && cached is not null) return cached;

        var shares = s.Query(Cim.SmbNs, "SELECT * FROM MSFT_SmbShare");

        var list = shares.Select(sh =>
        {
            var aces = new List<ShareAce>();
            // GetAccessControlEntries is an instance method; MSFT_SmbShareAccess
            // does not exist as a class, so this is the only route to the ACL.
            try
            {
                var res = s.InvokeInstance(sh, "GetAccessControlEntries");
                foreach (var e in res.Instances("Output"))
                {
                    aces.Add(new ShareAce
                    {
                        AccountName = Cim.Str(e, "AccountName"),
                        AccessRight = Cim.Int(e, "AccessRight"),
                        AccessControlType = Cim.Int(e, "AccessControlType"),
                    });
                }
            }
            catch { /* a share we cannot read the ACL of is not fatal */ }

            return new ShareInfo
            {
                Name = Cim.Str(sh, "Name"),
                Path = Cim.Str(sh, "Path"),
                Description = Cim.Str(sh, "Description"),
                EncryptData = Cim.Bool(sh, "EncryptData"),
                Special = Cim.Bool(sh, "Special"),
                CurrentUsers = Cim.Int(sh, "CurrentUsers"),
                ShareState = Cim.Int(sh, "ShareState"),
                Access = aces,
            };
        }).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();

        var sessions = s.Query(Cim.SmbNs, "SELECT * FROM MSFT_SmbSession")
            .Select(x => new SmbClientInfo
            {
                Client = Cim.Str(x, "ClientComputerName"),
                User = Cim.Str(x, "ClientUserName"),
                Opens = Cim.Int(x, "NumOpens"),
            }).ToList();

        var openFiles = s.Query(Cim.SmbNs, "SELECT * FROM MSFT_SmbOpenFile").Count;

        var snap = new SharesSnapshot { Shares = list, Sessions = sessions, OpenFiles = openFiles };
        s.Cache.Set("shares", snap);
        return snap;
    });

    public Task<OpResult> CreateShareAsync(WinnasSession s, string name, string path,
        string description, bool readOnly, string account) => Task.Run(() =>
    {
        try
        {
            if (string.IsNullOrWhiteSpace(name)) return OpResult.Fail("共享名不能为空");
            if (string.IsNullOrWhiteSpace(path)) return OpResult.Fail("路径不能为空");
            if (name.Contains('\\') || name.Contains('/')) return OpResult.Fail("共享名不能包含路径分隔符");

            var args = new Dictionary<string, object>
            {
                ["Name"] = name.Trim(),
                ["Path"] = path.Trim(),
                ["Description"] = description.Trim(),
                ["ConcurrentUserLimit"] = 0u,
                ["EncryptData"] = false,
            };

            var who = string.IsNullOrWhiteSpace(account) ? "Everyone" : account.Trim();
            if (readOnly) args["ReadAccess"] = new[] { who };
            else args["FullAccess"] = new[] { who };

            var res = s.InvokeStatic(Cim.SmbNs, "MSFT_SmbShare", "CreateShare", args);
            var rc = res.ReturnCode();
            if (rc != 0) return OpResult.Fail(ShareError(rc));

            // New-SmbShare in the web console also granted Everyone read, then
            // upgraded a named account. Mirror that so behaviour matches.
            // The out-parameter object has no instance path, so it is looked up
            // again by name — an instance method cannot be called without one.
            var createdName = res.Instances("CreatedShare").FirstOrDefault() is { } c ? Cim.Str(c, "Name") : "";
            var created = createdName.Length == 0
                ? null
                : s.First(Cim.SmbNs, $"SELECT * FROM MSFT_SmbShare WHERE Name='{createdName.Replace("'", "''")}'");
            if (created is not null && !readOnly && !string.IsNullOrWhiteSpace(account))
            {
                var grant = s.InvokeInstance(created, "GrantAccess", new Dictionary<string, object>
                {
                    ["AccountName"] = new[] { account.Trim() },
                    ["AccessRight"] = 0u,
                });
                var grc = grant.ReturnCode();
                if (grc != 0) return OpResult.Fail($"共享已创建，但授予权限失败：{ShareError(grc)}");
            }

            s.Cache.Invalidate("shares");
            s.Cache.Invalidate("overview");
            return OpResult.Good($"已创建共享 {name.Trim()}");
        }
        catch (Exception ex) { return OpResult.From(ex); }
    });

    public Task<OpResult> DeleteShareAsync(WinnasSession s, string name) => Task.Run(() =>
    {
        try
        {
            // MSFT_SmbShare has no delete method. Win32_Share.Delete() does.
            // Filtered in memory rather than by WQL string to avoid injection.
            var target = s.Query(Cim.CimV2, "SELECT * FROM Win32_Share")
                .FirstOrDefault(x => string.Equals(Cim.Str(x, "Name"), name, StringComparison.OrdinalIgnoreCase));

            if (target is null) return OpResult.Fail($"未找到共享：{name}");

            var res = s.InvokeInstance(target, "Delete");
            var rc = res.ReturnCode();
            if (rc != 0) return OpResult.Fail(ShareError(rc));

            s.Cache.Invalidate("shares");
            s.Cache.Invalidate("overview");
            return OpResult.Good($"已删除共享 {name}");
        }
        catch (Exception ex) { return OpResult.From(ex); }
    });

    private static string ShareError(uint rc) => rc switch
    {
        0 => "成功",
        5 => "拒绝访问：需要目标机上的管理员权限",
        87 => "参数无效：请检查共享名与路径",
        183 => "同名共享已存在",
        0x80070032 => "请求不受支持",
        _ => $"操作失败，返回码 0x{rc:X8}",
    };

    /// <summary>
    /// Drive letter to the volume's device path (\\?\Volume{guid}\).
    /// Win32_ShadowCopy.VolumeName speaks device paths while users speak drive
    /// letters, so this is the only way to tell which snapshots belong to D:.
    /// </summary>
    public Task<Dictionary<string, string>> GetVolumeDeviceMapAsync(WinnasSession s) => Task.Run(() =>
    {
        if (s.Cache.TryGet<Dictionary<string, string>>("volumeDeviceMap", SnapshotsTtl, out var cached)
            && cached is not null)
            return cached;

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in s.Query(Cim.StorageNs, "SELECT * FROM MSFT_Volume"))
        {
            var letter = Cim.DriveLetter(v, "DriveLetter");
            var path = Cim.Str(v, "Path");
            if (letter.Length == 0 || path.Length == 0) continue;
            map[letter] = path;
        }

        s.Cache.Set("volumeDeviceMap", map);
        return map;
    });

    public Task<List<string>> GetVolumeLettersAsync(WinnasSession s) => Task.Run(() =>
    {
        if (s.Cache.TryGet<List<string>>("volumeLetters", SnapshotsTtl, out var cached) && cached is not null)
            return cached;

        var list = s.Query(Cim.StorageNs, "SELECT * FROM MSFT_Volume")
            .Where(v => Cim.DriveLetter(v, "DriveLetter").Length > 0)
            .Select(v => Cim.DriveLetter(v, "DriveLetter") + ":\\")
            .Distinct()
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        s.Cache.Set("volumeLetters", list);
        return list;
    });

    // ----------------------------------------------------------- snapshots

    public Task<List<SnapshotInfo>> GetSnapshotsAsync(WinnasSession s) => Task.Run(() =>
    {
        if (s.Cache.TryGet<List<SnapshotInfo>>("snapshots", SnapshotsTtl, out var cached) && cached is not null)
            return cached;

        var list = s.Query(Cim.CimV2, "SELECT * FROM Win32_ShadowCopy")
            .OrderByDescending(x => Cim.Date(x, "InstallDate") ?? DateTime.MinValue)
            .Select(x => new SnapshotInfo
            {
                Id = Cim.Str(x, "ID"),
                VolumeName = Cim.Str(x, "VolumeName"),
                DeviceObject = Cim.Str(x, "DeviceObject"),
                InstallDate = Cim.Date(x, "InstallDate"),
                ClientAccessible = Cim.Bool(x, "ClientAccessible"),
                Persistent = Cim.Bool(x, "Persistent"),
                Differential = Cim.Bool(x, "Differential"),
            }).ToList();

        s.Cache.Set("snapshots", list);
        return list;
    });

    public Task<OpResult> CreateSnapshotAsync(WinnasSession s, string volume) => Task.Run(() =>
    {
        try
        {
            var vol = volume.Trim();
            if (vol.Length == 2 && vol[1] == ':') vol += "\\";
            if (!vol.EndsWith('\\')) vol += "\\";

            var res = s.InvokeStatic(Cim.CimV2, "Win32_ShadowCopy", "Create", new Dictionary<string, object>
            {
                ["Volume"] = vol,
                ["Context"] = "ClientAccessible",
            });

            var rc = res.ReturnCode();
            if (rc != 0) return OpResult.Fail(SnapshotError(rc));

            s.Cache.Invalidate("snapshots");
            return OpResult.Good($"已在 {vol} 创建卷影副本");
        }
        catch (Exception ex) { return OpResult.From(ex); }
    });

    public Task<OpResult> DeleteSnapshotAsync(WinnasSession s, string id) => Task.Run(() =>
    {
        try
        {
            var target = s.Query(Cim.CimV2, "SELECT * FROM Win32_ShadowCopy")
                .FirstOrDefault(x => string.Equals(Cim.Str(x, "ID"), id, StringComparison.OrdinalIgnoreCase));

            if (target is null) return OpResult.Fail("未找到该卷影副本");

            s.DeleteInstance(target);
            s.Cache.Invalidate("snapshots");
            return OpResult.Good("已删除卷影副本");
        }
        catch (Exception ex) { return OpResult.From(ex); }
    });

    private static string SnapshotError(uint rc) => rc switch
    {
        0 => "成功",
        1 => "拒绝访问：需要管理员权限",
        2 => "目标卷不支持卷影副本",
        3 => "指定的卷不存在",
        4 => "系统资源不足，无法创建卷影副本",
        8 => "卷影副本数量已达上限（每卷最多 64 个）",
        _ => $"创建失败，返回码 0x{rc:X8}",
    };

    // -------------------------------------------------------------- system

    public Task<SystemInfo> GetSystemAsync(WinnasSession s) => Task.Run(() =>
    {
        if (s.Cache.TryGet<SystemInfo>("system", SystemTtl, out var cached) && cached is not null) return cached;

        var watched = AppState.Settings.WatchedServices;
        var services = s.Query(Cim.CimV2, "SELECT * FROM Win32_Service")
            .Where(x => watched.Count == 0 || watched.Contains(Cim.Str(x, "Name"), StringComparer.OrdinalIgnoreCase))
            .Select(x => new ServiceInfo
            {
                Name = Cim.Str(x, "Name"),
                DisplayName = Cim.Str(x, "DisplayName"),
                State = Cim.Str(x, "State"),
                StartMode = Cim.Str(x, "StartMode"),
            })
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var addresses = s.Query(Cim.StdCimV2,
                "SELECT * FROM MSFT_NetIPAddress WHERE AddressFamily=2 AND IPAddress<>'127.0.0.1'")
            .Select(x => new IpInfo
            {
                Interface = Cim.Str(x, "InterfaceAlias"),
                Address = Cim.Str(x, "IPAddress"),
                Prefix = Cim.Int(x, "PrefixLength"),
            })
            .OrderBy(x => x.Interface, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var routes = s.Query(Cim.StdCimV2, "SELECT * FROM MSFT_NetRoute WHERE DestinationPrefix='0.0.0.0/0'")
            .Select(x => new RouteInfo
            {
                Interface = Cim.Str(x, "InterfaceAlias"),
                Gateway = Cim.Str(x, "NextHop"),
                Metric = Cim.Int(x, "RouteMetric"),
            }).ToList();

        var procs = s.Query(Cim.CimV2, "SELECT * FROM Win32_Process")
            .OrderByDescending(x => Cim.Long(x, "WorkingSetSize"))
            .Take(10)
            .Select(x => new ProcInfo
            {
                Name = Cim.Str(x, "Name"),
                Pid = Cim.Int(x, "ProcessId"),
                Memory = Cim.Long(x, "WorkingSetSize"),
            }).ToList();

        var info = new SystemInfo
        {
            Services = services,
            Addresses = addresses,
            Routes = routes,
            TopProcesses = procs,
        };

        s.Cache.Set("system", info);
        return info;
    });

    // -------------------------------------------------------------- backup

    public Task<BackupInfo> GetBackupAsync(WinnasSession s) => Task.Run(() =>
    {
        if (s.Cache.TryGet<BackupInfo>("backup", BackupTtl, out var cached) && cached is not null) return cached;

        var settings = AppState.Settings;
        var taskName = settings.BackupTask;

        var task = ReadTask(s, taskName);
        var robocopy = s.Query(Cim.CimV2, "SELECT * FROM Win32_Process WHERE Name='robocopy.exe'").Count > 0;

        // The source tree is slow to walk, so it never runs as part of a
        // refresh — only when the user asks, then it lives in the cache.
        long? srcFiles = null, srcBytes = null;
        if (s.Cache.TryGet<SourceStat>("backupSource", SourceTtl, out var stat) && stat is not null)
        {
            srcFiles = stat.Files;
            srcBytes = stat.Bytes;
        }

        var logs = ReadLogs(s);

        var volumes = s.Query(Cim.StorageNs, "SELECT * FROM MSFT_Volume")
            .Where(v => Cim.DriveLetter(v, "DriveLetter").Length > 0)
            .Select(v => new VolumeBrief
            {
                Drive = Cim.DriveLetter(v, "DriveLetter") + ":",
                Label = Cim.Str(v, "FileSystemLabel"),
                Size = Cim.Long(v, "Size"),
                Free = Cim.Long(v, "SizeRemaining"),
            }).ToList();

        var info = new BackupInfo
        {
            Task = task,
            RobocopyRunning = robocopy,
            SourceFiles = srcFiles,
            SourceBytes = srcBytes,
            SourceStale = srcFiles is null,
            Logs = logs,
            Volumes = volumes,
        };

        s.Cache.Set("backup", info);
        return info;
    });

    private static TaskInfo? ReadTask(WinnasSession s, string taskName)
    {
        if (string.IsNullOrWhiteSpace(taskName)) return null;

        if (!s.IsRemote)
        {
            try
            {
                var fromCom = TaskSchedulerCom.Get(taskName, new TaskSchedulerCom.Credential());
                if (fromCom is not null) return fromCom;
            }
            catch { /* fall through to CIM */ }
        }

        // Remote (or COM-unavailable): CIM only knows name/path/state.
        var simple = s.Query(Cim.TaskNs, "SELECT * FROM MSFT_ScheduledTask")
            .FirstOrDefault(x => string.Equals(Cim.Str(x, "TaskName"), taskName, StringComparison.OrdinalIgnoreCase)
                                 || string.Equals("\\" + Cim.Str(x, "TaskName"), taskName, StringComparison.OrdinalIgnoreCase));

        return simple is null ? null : new TaskInfo
        {
            Path = Cim.Str(simple, "TaskPath") + Cim.Str(simple, "TaskName"),
            State = Cim.Int(simple, "State"),
            Enabled = Cim.Int(simple, "State") != 1,
        };
    }

    private static List<LogFileInfo> ReadLogs(WinnasSession s)
    {
        var dir = AppState.Settings.BackupLogDir;
        if (string.IsNullOrWhiteSpace(dir)) return new List<LogFileInfo>();

        var access = PathFor(s, dir);
        try
        {
            using var _ = s.Impersonate();
            if (!Directory.Exists(access)) return new List<LogFileInfo>();
            return new DirectoryInfo(access)
                .EnumerateFiles("*.log")
                .OrderByDescending(f => f.LastWriteTime)
                .Take(12)
                .Select(f => new LogFileInfo
                {
                    Name = f.Name,
                    Size = f.Length,
                    LastWrite = f.LastWriteTime,
                }).ToList();
        }
        catch { return new List<LogFileInfo>(); }
    }

    /// <summary>
    /// Local paths are meaningless on a remote target, so flip D:\NAS into
    /// \\host\D$\NAS when we are talking to another machine. Sources are only
    /// reachable this way through the built-in admin shares.
    /// </summary>
    public static string PathFor(WinnasSession s, string localPath)
    {
        if (string.IsNullOrWhiteSpace(localPath)) return localPath;
        if (!s.IsRemote || localPath.StartsWith(@"\\")) return localPath;
        if (localPath.Length >= 2 && localPath[1] == ':')
            return $@"\\{s.Profile.Target}\{localPath[0]}${localPath[2..]}";
        return localPath;
    }

    public Task<(long Files, long Bytes)> MeasureSourceAsync(WinnasSession s) => Task.Run(() =>
    {
        using var _ = s.Impersonate();
        var dir = PathFor(s, AppState.Settings.BackupSource);
        long files = 0, bytes = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", new System.IO.EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            }))
            {
                files++;
                try
                {
                    var len = new FileInfo(f).Length;
                    if (len > 0) bytes += len;
                }
                catch { /* locked file, skip */ }
            }
        }
        catch { /* unreachable share: caller shows the reason */ }

        s.Cache.Set("backupSource", new SourceStat(files, bytes));
        s.Cache.Invalidate("backup");
        return (files, bytes);
    });

    public Task<OpResult> RunBackupTaskAsync(WinnasSession s) => Task.Run(() =>
    {
        try
        {
            var name = AppState.Settings.BackupTask;
            if (string.IsNullOrWhiteSpace(name)) return OpResult.Fail("尚未配置备份任务名");

            if (!s.IsRemote)
            {
                var ok = TaskSchedulerCom.Run(name, new TaskSchedulerCom.Credential());
                if (!ok) return OpResult.Fail("无法触发任务，请确认任务存在且你有权限运行它");
            }
            else
            {
                // CIM exposes no Run method on MSFT_ScheduledTask; COM over DCOM
                // to a remote host is unreliable, so report the limitation
                // instead of pretending it worked.
                return OpResult.Fail("远程触发任务计划需要目标机启用 WinRM 并以管理员身份连接。当前请在本机控制台上触发，或改用 WinRM 传输。");
            }

            s.Cache.Invalidate("backup");
            return OpResult.Good($"已触发任务 {name}");
        }
        catch (Exception ex) { return OpResult.From(ex); }
    });

    public Task<string[]> ReadLogTailAsync(WinnasSession s, string logName, int maxLines = 200) => Task.Run(() =>
    {
        var baseDir = PathFor(s, AppState.Settings.BackupLogDir);
        var full = Path.Combine(baseDir, Path.GetFileName(logName));
        try
        {
            var lines = System.IO.File.ReadAllLines(full);
            return lines.Skip(Math.Max(0, lines.Length - maxLines)).ToArray();
        }
        catch { return Array.Empty<string>(); }
    });
}
