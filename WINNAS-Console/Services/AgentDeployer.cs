using System.IO;
using System.Text.Json;
using Winnas.Console.Infrastructure;
using Winnas.Console.Models;

namespace Winnas.Console.Services;

/// <summary>
/// Puts a runnable copy of this program on the managed machine.
///
/// A scheduled task must execute on the machine it is supposed to act on. When
/// the console lives on the desktop and the NAS is the target, registering a
/// task whose /TR points at the desktop's own EXE path produces a task that
/// fails every hour on a path that does not exist there. So the EXE (and the
/// policy definitions it needs) are copied over once, and the task points at
/// that copy.
/// </summary>
public static class AgentDeployer
{
    public const string AgentDir = @"C:\ProgramData\WINNAS-Console\agent";

    public static string AgentExe => AgentDir + @"\WINNAS.Console.exe";
    public static string PolicyFile => AgentDir + @"\snapshot-policies.json";
    public static string PlanFile => AgentDir + @"\backup-plans.json";

    public static string LocalExe => Environment.ProcessPath ?? "";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>
    /// Returns the path the task should run on the target. For a local target
    /// that is simply the EXE already running.
    /// </summary>
    public static Task<(OpResult Result, string Path)> EnsureAsync(WinnasSession s) => Task.Run(() =>
    {
        var local = LocalExe;
        if (local.Length == 0) return (OpResult.Fail("无法确定程序自身路径"), "");

        if (!s.IsRemote) return (OpResult.Good("本机运行，直接使用当前程序"), local);

        try
        {
            using var _ = s.Impersonate();
            var remote = WinnasService.PathFor(s, AgentExe);
            var source = new FileInfo(local);

            // Same size is a good enough identity check for "already deployed";
            // re-copying 60 MB on every registration would be rude.
            if (File.Exists(remote) && new FileInfo(remote).Length == source.Length)
                return (OpResult.Good("被控端已有执行器"), AgentExe);

            Directory.CreateDirectory(WinnasService.PathFor(s, AgentDir));
            File.Copy(local, remote, overwrite: true);

            var copied = new FileInfo(remote).Length;
            return copied == source.Length
                ? (OpResult.Good($"已把执行器部署到被控端（{Format.Bytes(copied)}）"), AgentExe)
                : (OpResult.Fail("部署后文件大小不一致，复制可能被中断"), "");
        }
        catch (Exception ex)
        {
            return (OpResult.Fail(
                "无法把执行器复制到被控端：" + OpResult.From(ex).Message +
                "。定时任务必须在被控端本机执行，所以需要能访问它的管理共享（\\\\主机\\C$）。" +
                "如果访问不了，请改用「导出脚本」，把 EXE 和脚本一起拷到被控端手动部署。"), "");
        }
    });

    /// <summary>
    /// The agent cannot read the console's config.json — different machine,
    /// different user profile. The policy definitions travel with it.
    /// </summary>
    public static Task<OpResult> WritePoliciesAsync(WinnasSession s, IReadOnlyList<SnapshotPolicy> policies)
        => Task.Run(() => Write(s, PolicyFile, policies));

    public static Task<OpResult> WritePlansAsync(WinnasSession s, IReadOnlyList<BackupPlan> plans)
        => Task.Run(() => Write(s, PlanFile, plans));

    private static OpResult Write<T>(WinnasSession s, string targetPath, IReadOnlyList<T> items)
    {
        try
        {
            var path = WinnasService.PathFor(s, targetPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(items, Json));
            return OpResult.Good("已同步定义到被控端");
        }
        catch (Exception ex)
        {
            return OpResult.Fail("同步定义失败：" + OpResult.From(ex).Message);
        }
    }

    /// <summary>
    /// When this process IS the deployed agent, the policies live in the file
    /// next to the EXE. Called by the headless entry points before they run.
    /// </summary>
    public static void AdoptSidecarPolicies()
    {
        var list = ReadSidecar<SnapshotPolicy>("snapshot-policies.json");
        if (list is not null) AppState.Settings.SnapshotPolicies = list;
    }

    public static void AdoptSidecarPlans()
    {
        var list = ReadSidecar<BackupPlan>("backup-plans.json");
        if (list is not null) AppState.Settings.BackupPlans = list;
    }

    // --------------------------------------------------- agent-side reading

    /// <summary>
    /// Reads a definition file sitting next to this EXE. Present only when the
    /// process IS the deployed agent; a normal desktop launch sees nothing and
    /// falls back to its own config.
    /// </summary>
    public static List<T>? ReadSidecar<T>(string fileName)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, fileName);
            if (!File.Exists(path)) return null;

            var list = JsonSerializer.Deserialize<List<T>>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            return list is { Count: > 0 } ? list : null;
        }
        catch { return null; }
    }
}

/// <summary>
/// schtasks.exe, addressed at the target. Both schedulers share this so the
/// /S /U /P rules live in exactly one place.
/// </summary>
public static class Schtasks
{
    public static (int Code, string Out, string Err) Run(WinnasSession s, string args, int timeoutMs = 20000)
    {
        if (s.IsRemote)
        {
            var host = s.Profile.Target.Trim();
            if (host.Length > 0) args += $" /S \"{host}\"";

            var user = s.Profile.UserName.Trim();
            var pwd = s.Profile.GetPassword();

            // Only send credentials when they exist — an empty /P makes
            // schtasks sit there prompting forever.
            if (user.Length > 0 && pwd is not null)
            {
                var domain = s.Profile.Domain.Trim();
                var account = domain.Length > 0 ? $"{domain}\\{user}" : user;
                args += $" /U \"{account}\" /P \"{SecretVault.ConsumeToPlain(pwd)}\"";
            }
        }

        return ProcessRunner.Run("schtasks.exe", args, ProcessRunner.Oem, timeoutMs);
    }
}
