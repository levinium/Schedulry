namespace Schedulry.Core;

/// <summary>What Task Scheduler says a task is doing right now.</summary>
public enum RunState
{
    Unknown,
    Disabled,
    Queued,
    Ready,
    Running,
}

/// <summary>How the task's account signs in when it runs (the "Security options" in the old window).</summary>
public enum LogonKind
{
    /// <summary>Only while the user is signed in, on their desktop.</summary>
    Interactive,
    /// <summary>Whether or not the user is signed in, with a stored password.</summary>
    Password,
    /// <summary>Whether or not the user is signed in, without a stored password (no network or encrypted files).</summary>
    S4U,
    /// <summary>A built-in service account (SYSTEM, LOCAL SERVICE, NETWORK SERVICE).</summary>
    ServiceAccount,
    /// <summary>Any member of a group, while signed in.</summary>
    Group,
    /// <summary>Interactive, falling back to the stored password.</summary>
    InteractiveOrPassword,
    Unknown,
}

public enum ActionKind
{
    Program,
    Email,
    Message,
    ComHandler,
}

/// <summary>One thing a task does when it runs.</summary>
public sealed record ActionInfo(ActionKind Kind, string Path, string? Arguments = null, string? WorkingDirectory = null)
{
    /// <summary>The command as it would be typed: path in quotes when it has spaces, then the arguments.</summary>
    public string CommandLine
    {
        get
        {
            var path = Path.Contains(' ') && !Path.StartsWith('"') ? $"\"{Path}\"" : Path;
            return string.IsNullOrWhiteSpace(Arguments) ? path : $"{path} {Arguments}";
        }
    }

    public string Summary => Kind switch
    {
        ActionKind.Program => CommandLine,
        ActionKind.ComHandler => $"Runs a built-in Windows handler ({Path})",
        ActionKind.Email => "Sends an email (no longer supported by Windows)",
        ActionKind.Message => "Shows a message (no longer supported by Windows)",
        _ => Path,
    };
}

/// <summary>The settings people most often need to see, in the order the editor shows them.</summary>
public sealed record TaskSettingsInfo
{
    public TimeSpan? StopAfter { get; init; }
    public bool RunMissedStart { get; init; }
    public bool RestartOnFailure { get; init; }
    public TimeSpan RestartInterval { get; init; }
    public int RestartCount { get; init; }
    /// <summary>What happens when it's due while still running: Parallel, Queue, IgnoreNew or StopExisting.</summary>
    public string MultipleInstances { get; init; } = "IgnoreNew";
    public bool OnlyOnAcPower { get; init; }
    public bool OnlyWhenIdle { get; init; }
    public bool OnlyWithNetwork { get; init; }
    public bool WakeToRun { get; init; }
    public bool Hidden { get; init; }
    public bool AllowRunOnDemand { get; init; } = true;
}

/// <summary>
/// One registered task, read in full. Records are replaced whole when anything changes, so a view
/// can compare old and new to see what moved.
/// </summary>
public sealed record TaskSnapshot
{
    /// <summary>Full path, such as \Backups\Nightly copy. The root folder is "\".</summary>
    public required string Path { get; init; }
    public string Name => Path[(Path.LastIndexOf('\\') + 1)..];
    public string Folder => Path.LastIndexOf('\\') <= 0 ? "\\" : Path[..Path.LastIndexOf('\\')];

    public string? Description { get; init; }
    public string? Author { get; init; }
    public DateTime? Created { get; init; }

    public RunState State { get; init; }
    public bool Enabled { get; init; } = true;
    public DateTime? LastRun { get; init; }
    public int? LastResult { get; init; }
    public DateTime? NextRun { get; init; }
    /// <summary>When the current run started, while <see cref="State"/> is Running.</summary>
    public DateTime? RunningSince { get; init; }
    /// <summary>The program running now, while <see cref="State"/> is Running.</summary>
    public string? CurrentAction { get; init; }

    public string RunAs { get; init; } = "";
    public LogonKind Logon { get; init; }
    public bool HighestPrivileges { get; init; }

    public IReadOnlyList<TriggerSpec> Triggers { get; init; } = [];
    public IReadOnlyList<ActionInfo> Actions { get; init; } = [];
    public TaskSettingsInfo Settings { get; init; } = new();

    /// <summary>Windows' own tasks, under \Microsoft\.</summary>
    public bool IsWindowsTask => Path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase);

    public TaskOutcome Outcome => State == RunState.Running ? TaskOutcome.Running : ResultCodes.Classify(LastResult, LastRun);

    /// <summary>True when only the live fields differ: state, times and result.</summary>
    public bool SameDefinition(TaskSnapshot other) =>
        Path == other.Path && Description == other.Description && Author == other.Author && RunAs == other.RunAs
        && Logon == other.Logon && HighestPrivileges == other.HighestPrivileges && Enabled == other.Enabled
        && Settings == other.Settings && Triggers.SequenceEqual(other.Triggers) && Actions.SequenceEqual(other.Actions);
}

/// <summary>
/// The fields a status sweep reads, cheaper than a full read. A host may leave <see cref="NextRun"/>
/// null when it's costly; the monitor then re-reads a task in full whenever the other fields move.
/// </summary>
public sealed record TaskStatusInfo(string Path, RunState State, bool Enabled, DateTime? LastRun, int? LastResult, DateTime? NextRun);

/// <summary>A task instance running right now.</summary>
public sealed record RunningInstance(string Path, DateTime? Since, string? CurrentAction, int? ProcessId);
