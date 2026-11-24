using Winnas.Console.Infrastructure;

namespace Winnas.Console.Models;

/// <summary>What the remote-control page can do to the managed machine.</summary>
public enum PowerAction
{
    Shutdown,
    Reboot,
    Logoff,
    Hibernate,
    Sleep,
    Lock,
    Abort,
}

public sealed class RemoteProcess
{
    public string Name { get; init; } = "";
    public int Pid { get; init; }
    public long Memory { get; init; }
    public string? ExecutablePath { get; init; }
    public string? CommandLine { get; init; }
    public DateTime? Created { get; init; }

    /// <summary>
    /// Ending one of these takes the machine down with it. The UI refuses
    /// instead of letting a mis-click reboot the NAS.
    /// </summary>
    public bool Protected { get; set; }

    public string MemoryText => Format.Bytes(Memory);
    public string CreatedText => Created is null ? "—" : Created.Value.ToString("MM-dd HH:mm");
}

public sealed class RemoteService
{
    public string Name { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string State { get; init; } = "";
    public string StartMode { get; init; } = "";
    public bool AcceptStop { get; init; }
    public bool AcceptPause { get; init; }
    public string? PathName { get; init; }

    public bool Running => string.Equals(State, "Running", StringComparison.OrdinalIgnoreCase);
    public bool Stopped => string.Equals(State, "Stopped", StringComparison.OrdinalIgnoreCase);

    public string StateText => State switch
    {
        "Running" => "运行中",
        "Stopped" => "已停止",
        "Start Pending" => "启动中",
        "Stop Pending" => "停止中",
        "Paused" => "已暂停",
        _ => State,
    };

    public string StartModeText => StartMode switch
    {
        "Auto" => "自动",
        "Manual" => "手动",
        "Disabled" => "禁用",
        _ => StartMode,
    };

    /// <summary>The four services that keep the box reachable over SMB/WMI.</summary>
    public bool Critical =>
        Name is "LanmanServer" or "LanmanWorkstation" or "Winmgmt" or "RpcSs" or "TermService";
}

/// <summary>Result of a command executed on the managed machine.</summary>
public sealed class RemoteCommandResult
{
    public bool Ok { get; init; }
    public int ExitCode { get; init; }
    public string Output { get; init; } = "";
    public string CommandLine { get; init; } = "";
    public string Message { get; init; } = "";
    public double Seconds { get; init; }
    public string? OutputPath { get; init; }

    public string HeaderText =>
        $"{(Ok ? "完成" : "失败")} · 用时 {Seconds:0.0}s · 退出码 {ExitCode}";
}

/// <summary>One entry in the "run again" history of the command box.</summary>
public sealed class CommandPreset
{
    public string Name { get; init; } = "";
    public string Command { get; init; } = "";
    public string? Note { get; init; }
}
