using System.Runtime.InteropServices;
using System.Security;
using Winnas.Console.Models;

namespace Winnas.Console.Infrastructure;

/// <summary>
/// Bridge to the Task Scheduler 2.0 COM object (Schedule.Service).
///
/// Why COM at all: the CIM class MSFT_ScheduledTask exposes only TaskName,
/// TaskPath and State — no LastRunTime, no NextRunTime, no LastTaskResult and
/// no Run method. Everything a backup page actually needs therefore comes from
/// COM. It is local-only in practice; for remote targets we fall back to CIM.
/// </summary>
public static class TaskSchedulerCom
{
    public sealed class Credential
    {
        public string? Computer { get; init; }
        public string? User { get; init; }
        public string? Domain { get; init; }
        public SecureString? Password { get; init; }
        public bool IsRemote => !string.IsNullOrWhiteSpace(Computer);
    }

    private static Type? ComType() => Type.GetTypeFromProgID("Schedule.Service");

    public static bool Available => ComType() is not null;

    private static dynamic? Connect(Credential cred)
    {
        var type = ComType();
        if (type is null) return null;
        dynamic svc = Activator.CreateInstance(type)!;
        if (!cred.IsRemote)
        {
            svc.Connect();
            return svc;
        }
        var plain = cred.Password is null ? "" : SecretVault.ConsumeToPlain(cred.Password);
        svc.Connect(cred.Computer, cred.User, cred.Domain, plain);
        return svc;
    }

    public static TaskInfo? Get(string taskPath, Credential cred)
    {
        var svc = Connect(cred);
        if (svc is null) return null;
        try
        {
            dynamic folder = svc.GetFolder("\\");
            dynamic task = folder.GetTask(Normalize(taskPath));
            return Map(task);
        }
        finally { Release(svc); }
    }

    public static List<TaskInfo> ListAll(Credential cred, int limit = 400)
    {
        var result = new List<TaskInfo>();
        var svc = Connect(cred);
        if (svc is null) return result;
        try
        {
            var root = (string)svc.GetFolder("\\").Path;
            Walk(svc, root, result, limit);
        }
        finally { Release(svc); }
        return result;
    }

    public static bool Run(string taskPath, Credential cred)
    {
        var svc = Connect(cred);
        if (svc is null) return false;
        try
        {
            dynamic folder = svc.GetFolder("\\");
            dynamic task = folder.GetTask(Normalize(taskPath));
            task.Run(null);
            return true;
        }
        finally { Release(svc); }
    }

    /// <summary>Enumerate every task name once, for the settings picker.</summary>
    public static List<string> Names(Credential cred, int limit = 400)
        => ListAll(cred, limit).Select(t => t.Path).ToList();

    private static void Walk(dynamic svc, string folderPath, List<TaskInfo> sink, int limit)
    {
        if (sink.Count >= limit) return;
        dynamic folder = svc.GetFolder(folderPath);
        foreach (var task in folder.GetTasks(1))
        {
            if (sink.Count >= limit) return;
            try { sink.Add(Map(task)); } catch { /* skip malformed entries */ }
        }
        foreach (var sub in folder.GetFolders(0))
        {
            if (sink.Count >= limit) return;
            try { Walk(svc, (string)sub.Path, sink, limit); }
            catch { /* access denied on protected folders is expected */ }
        }
    }

    private static TaskInfo Map(dynamic task) => new()
    {
        Path = (string)task.Path,
        State = (int)task.State,
        Enabled = (bool)task.Enabled,
        LastRun = TrimImpossible(ReadDate(task.LastRunTime)),
        NextRun = TrimImpossible(ReadDate(task.NextRunTime)),
        LastResult = (long)(int)task.LastTaskResult,
    };

    private static DateTime? ReadDate(object? raw) =>
        raw is DateTime dt ? dt : null;

    /// <summary>Task Scheduler reports 1899-12-30 for "never run".</summary>
    private static DateTime? TrimImpossible(DateTime? dt) =>
        dt is null || dt.Value.Year < 1900 ? null : dt;

    private static string Normalize(string path)
    {
        path = (path ?? "").Trim();
        if (path.Length == 0) return "\\";
        return path.StartsWith('\\') ? path : "\\" + path;
    }

    private static void Release(dynamic com)
    {
        try { Marshal.FinalReleaseComObject(com); } catch { /* best effort */ }
    }
}
