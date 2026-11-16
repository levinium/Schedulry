namespace Schedulry.Core;

/// <summary>
/// One entry from Task Scheduler's history log, reduced to what building runs needs. Every event
/// of one run shares an activity ID.
/// </summary>
public sealed record HistoryEvent(int Id, DateTime Time, Guid ActivityId, string TaskName, int? ResultCode = null, string? Detail = null);

/// <summary>How a run came about.</summary>
public enum RunCause
{
    Unknown,
    Schedule,
    Person,
    Startup,
    LogOn,
    Event,
    Registration,
    Idle,
    SessionChange,
}

/// <summary>How a run ended (or that it hasn't yet).</summary>
public enum RunEnd
{
    Running,
    Succeeded,
    Failed,
    /// <summary>Ended by a person, by its time limit, or by Windows.</summary>
    Stopped,
    /// <summary>Never started: the previous run was still going, or conditions weren't met.</summary>
    Skipped,
    /// <summary>Couldn't be started at all (account, program not found).</summary>
    FailedToStart,
    /// <summary>The log has its start but not its end (log trimmed, or Windows restarted).</summary>
    Unknown,
}

/// <summary>One run of one task, put together from its history events.</summary>
public sealed record RunRecord
{
    public required string Path { get; init; }
    public required Guid ActivityId { get; init; }
    public required DateTime Start { get; init; }
    public DateTime? End { get; init; }
    public RunCause Cause { get; init; }
    public RunEnd Outcome { get; init; }
    public int? ResultCode { get; init; }
    public string? Program { get; init; }

    public string Name => Path[(Path.LastIndexOf('\\') + 1)..];
    public TimeSpan? Duration => End is { } end ? end - Start : null;

    public string CauseText => Cause switch
    {
        RunCause.Schedule => "On schedule",
        RunCause.Person => "Run by hand",
        RunCause.Startup => "At start-up",
        RunCause.LogOn => "At sign-in",
        RunCause.Event => "By an event",
        RunCause.Registration => "When saved",
        RunCause.Idle => "When idle",
        RunCause.SessionChange => "On a session change",
        _ => "",
    };

    /// <summary>"Succeeded", "Exit code 1", "Skipped: still running"…</summary>
    public string OutcomeText => Outcome switch
    {
        RunEnd.Running => "Running",
        RunEnd.Skipped => "Skipped: still running or conditions not met",
        RunEnd.Stopped => "Stopped",
        RunEnd.Unknown => "End not recorded",
        _ when ResultCode is { } code => ResultCodes.Describe(code).Short,
        RunEnd.Succeeded => "Succeeded",
        RunEnd.FailedToStart => "Couldn't start",
        _ => "Failed",
    };
}

/// <summary>
/// Turns history events into runs. Pure, so it's tested without Windows. Events can arrive in any
/// order; the newest run comes first in the result.
/// </summary>
public static class RunHistory
{
    // Task Scheduler's event IDs (Microsoft-Windows-TaskScheduler/Operational).
    public const int Started = 100, LaunchFailed = 101, Finished = 102, ActionStartFailed = 103,
        TriggeredBySchedule = 107, TriggeredByEvent = 108, TriggeredByRegistration = 109, TriggeredByPerson = 110,
        Terminated = 111, TriggeredAtStartup = 118, TriggeredAtLogOn = 119, TriggeredBySession = 120, TriggeredWhenIdle = 113,
        ProcessCreated = 129, ActionFinished = 201, ActionFailed = 202, ActionLaunchFailed = 203,
        LaunchIgnoredStillRunning = 322, ConditionsNotMet = 332;

    /// <summary>Every event ID that says something about a run; the reader filters the log to these.</summary>
    public static readonly int[] RunEventIds =
    [
        Started, LaunchFailed, Finished, ActionStartFailed, TriggeredBySchedule, TriggeredByEvent, TriggeredByRegistration,
        TriggeredByPerson, Terminated, TriggeredWhenIdle, TriggeredAtStartup, TriggeredAtLogOn, TriggeredBySession,
        ProcessCreated, ActionFinished, ActionFailed, ActionLaunchFailed, LaunchIgnoredStillRunning, ConditionsNotMet,
    ];

    public static IReadOnlyList<RunRecord> Build(IEnumerable<HistoryEvent> events)
    {
        var runs = new List<RunRecord>();
        foreach (var group in events.GroupBy(e => (e.ActivityId, Task: e.TaskName.ToUpperInvariant())))
        {
            var list = group.OrderBy(e => e.Time).ToList();
            var first = list[0];
            var start = list.FirstOrDefault(e => e.Id == Started)?.Time ?? first.Time;
            var finished = list.LastOrDefault(e => e.Id is Finished or Terminated or LaunchFailed or ActionLaunchFailed or ActionStartFailed or LaunchIgnoredStillRunning or ConditionsNotMet);
            // The program's exit code is on "action finished"; failures carry theirs on the failure event.
            var code = list.LastOrDefault(e => e.Id is ActionFinished or ActionFailed && e.ResultCode is not null)?.ResultCode
                       ?? list.LastOrDefault(e => e.ResultCode is not null)?.ResultCode;

            var outcome = finished?.Id switch
            {
                null => list.Any(e => e.Id is Started or ProcessCreated) ? RunEnd.Running : RunEnd.Unknown,
                LaunchIgnoredStillRunning or ConditionsNotMet => RunEnd.Skipped,
                LaunchFailed or ActionLaunchFailed or ActionStartFailed => RunEnd.FailedToStart,
                Terminated => RunEnd.Stopped,
                _ => code is null or 0 ? RunEnd.Succeeded : RunEnd.Failed,
            };

            runs.Add(new RunRecord
            {
                Path = first.TaskName,
                ActivityId = group.Key.ActivityId,
                Start = start,
                End = outcome is RunEnd.Running ? null : finished?.Time,
                Outcome = outcome,
                ResultCode = code,
                Program = list.FirstOrDefault(e => e.Id == ProcessCreated)?.Detail,
                Cause = list.Select(e => e.Id).FirstOrDefault(id => CauseOf(id) != RunCause.Unknown) is var id and not 0 ? CauseOf(id) : RunCause.Unknown,
            });
        }
        return runs.OrderByDescending(r => r.Start).ToList();
    }

    static RunCause CauseOf(int id) => id switch
    {
        TriggeredBySchedule => RunCause.Schedule,
        TriggeredByPerson => RunCause.Person,
        TriggeredAtStartup => RunCause.Startup,
        TriggeredAtLogOn => RunCause.LogOn,
        TriggeredByEvent => RunCause.Event,
        TriggeredByRegistration => RunCause.Registration,
        TriggeredWhenIdle => RunCause.Idle,
        TriggeredBySession => RunCause.SessionChange,
        _ => RunCause.Unknown,
    };
}
