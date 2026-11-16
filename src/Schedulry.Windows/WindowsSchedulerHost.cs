using System.Diagnostics.Eventing.Reader;
using System.Security.Principal;
using Microsoft.Win32.TaskScheduler;
using Schedulry.Core;
using TaskSnapshot = Schedulry.Core.TaskSnapshot;
using WinTask = Microsoft.Win32.TaskScheduler.Task;

namespace Schedulry.Windows;

/// <summary>
/// Task Scheduler on this computer (or, later, another one), through its COM API. Each thread gets
/// its own connection: the monitor reads on its thread while buttons act on pool threads, and COM
/// objects are not passed between them.
/// </summary>
public sealed class WindowsSchedulerHost : ISchedulerHost
{
    public const string HistoryLog = "Microsoft-Windows-TaskScheduler/Operational";

    readonly string? _computer;
    readonly ThreadLocal<TaskService> _service;
    readonly object _watchGate = new();
    EventLogWatcher? _watcher;
    bool _historyEnabled;
    DateTime _historyCheckedAt = DateTime.MinValue;

    public WindowsSchedulerHost(string? computer = null)
    {
        _computer = computer;
        _service = new ThreadLocal<TaskService>(() => new TaskService(_computer), trackAllValues: true);
        using var identity = WindowsIdentity.GetCurrent();
        CurrentUser = identity.Name;
        IsElevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    TaskService Service => _service.Value!;

    public string ComputerName => _computer ?? Environment.MachineName;
    public bool IsElevated { get; }
    public string CurrentUser { get; }

    public event Action<string>? TaskEvent;

    public IReadOnlyList<TaskSnapshot> ReadAll()
    {
        var list = new List<TaskSnapshot>();
        foreach (var task in AllTasks())
        {
            using (task)
            {
                try
                {
                    list.Add(Map(task));
                }
                catch (Exception e) when (e is UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
                {
                    // A task whose definition this account can't read: list it by name only.
                    list.Add(new TaskSnapshot { Path = task.Path, State = RunState.Unknown, Description = "This account can't read this task's details." });
                }
            }
        }
        return list;
    }

    public TaskSnapshot? Read(string path)
    {
        using var task = Find(path);
        return task is null ? null : Map(task);
    }

    public IReadOnlyList<TaskStatusInfo> ReadStatus(bool includeWindowsTasks = true)
    {
        var list = new List<TaskStatusInfo>();
        foreach (var task in AllTasks(includeWindowsTasks))
        {
            using (task)
            {
                try
                {
                    // No NextRunTime: Windows works it out on every call, and it was most of a sweep's cost.
                    var lastRun = Time(task.LastRunTime);
                    list.Add(new TaskStatusInfo(task.Path, MapState(task.State), task.Enabled, lastRun, lastRun is null ? null : task.LastTaskResult, null));
                }
                catch (Exception e) when (e is UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
                {
                    list.Add(new TaskStatusInfo(task.Path, RunState.Unknown, true, null, null, null));
                }
            }
        }
        return list;
    }

    public IReadOnlyList<RunningInstance> ReadRunning()
    {
        var list = new List<RunningInstance>();
        foreach (var running in Service.GetRunningTasks(includeHidden: true))
        {
            using (running)
            {
                string? action = null;
                DateTime? since = null;
                int? pid = null;
                try { action = running.CurrentAction; } catch (Exception) { /* not every instance reports it */ }
                try { since = Time(running.LastRunTime); } catch (Exception) { }
                try { pid = (int)running.EnginePID; } catch (Exception) { }
                list.Add(new RunningInstance(running.Path, since, action, pid));
            }
        }
        return list;
    }

    public IReadOnlyList<DateTime> NextRuns(string path, int count)
    {
        using var task = Find(path);
        if (task is null || !task.Enabled)
            return [];
        var now = DateTime.Now;
        return task.GetRunTimes(now, now.AddYears(1), (uint)count).ToList();
    }

    public string ReadXml(string path)
    {
        using var task = Find(path);
        return task?.Xml ?? "";
    }

    public bool HistoryEnabled
    {
        get
        {
            // Cheap, but asked every tick; ten seconds is soon enough to notice it being turned on.
            if (DateTime.UtcNow - _historyCheckedAt > TimeSpan.FromSeconds(10))
            {
                _historyCheckedAt = DateTime.UtcNow;
                try
                {
                    using var session = Session();
                    using var config = new EventLogConfiguration(HistoryLog, session);
                    _historyEnabled = config.IsEnabled;
                }
                catch (Exception e) when (e is EventLogException or UnauthorizedAccessException)
                {
                    _historyEnabled = false;
                }
                UpdateWatcher();
            }
            return _historyEnabled;
        }
    }

    public bool RecheckHistory()
    {
        _historyCheckedAt = DateTime.MinValue;
        return HistoryEnabled;
    }

    EventLogSession Session() => _computer is null ? new EventLogSession() : new EventLogSession(_computer);

    /// <summary>Follows the history log while it's on, so starts, finishes and edits show at once.</summary>
    void UpdateWatcher()
    {
        lock (_watchGate)
        {
            if (_historyEnabled && _watcher is null)
            {
                // 100 started, 101/103 failed to start, 102 finished, 106 created, 129 process started,
                // 140 changed, 141 deleted, 142 turned off, 201 action finished.
                var query = new EventLogQuery(HistoryLog, PathType.LogName,
                    "*[System[(EventID=100 or EventID=101 or EventID=102 or EventID=103 or EventID=106 or EventID=129 or EventID=140 or EventID=141 or EventID=142 or EventID=201)]]")
                {
                    Session = Session(),
                };
                _watcher = new EventLogWatcher(query);
                _watcher.EventRecordWritten += (_, e) =>
                {
                    using var record = e.EventRecord;
                    if (record?.Properties.Count > 0 && record.Properties[0].Value is string path && path.StartsWith('\\'))
                        TaskEvent?.Invoke(path);
                };
                try
                {
                    _watcher.Enabled = true;
                }
                catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException)
                {
                    _watcher.Dispose();
                    _watcher = null;
                }
            }
            else if (!_historyEnabled && _watcher is not null)
            {
                _watcher.Dispose();
                _watcher = null;
            }
        }
    }

    // Named fields of a history event, read the same way whatever the event's layout.
    static readonly EventLogPropertySelector HistoryFields = new(
    [
        "Event/EventData/Data[@Name='TaskName']",
        "Event/EventData/Data[@Name='ResultCode']",
        "Event/EventData/Data[@Name='Path']",
    ]);

    public IReadOnlyList<RunRecord> ReadRuns(string? path, DateTime since, int maxRuns)
    {
        if (!HistoryEnabled)
            return [];
        var ids = string.Join(" or ", RunHistory.RunEventIds.Select(id => $"EventID={id}"));
        var xpath = $"*[System[({ids}) and TimeCreated[@SystemTime>='{since.ToUniversalTime():yyyy-MM-ddTHH:mm:ss.fffZ}']]]";
        if (path is not null)
        {
            // XPath has no escape for quotes: pick the kind the path doesn't contain.
            var quoted = path.Contains('\'') ? $"\"{path}\"" : $"'{path}'";
            xpath += $" and *[EventData[Data[@Name='TaskName']={quoted}]]";
        }

        var events = new List<HistoryEvent>();
        var activities = new HashSet<Guid>();
        using var session = Session();
        var query = new EventLogQuery(HistoryLog, PathType.LogName, xpath) { ReverseDirection = true, Session = session };
        using var reader = new EventLogReader(query);
        // Newest first; stop once there are enough runs (plus one, whose start may be cut off).
        for (var read = 0; read < 50_000; read++)
        {
            using var record = (EventLogRecord?)reader.ReadEvent();
            if (record is null)
                break;
            if (record.ActivityId is not { } activity || record.TimeCreated is not { } time)
                continue;
            if (activities.Add(activity) && activities.Count > maxRuns + 1)
                break;
            var values = record.GetPropertyValues(HistoryFields);
            if (values[0] is not string task)
                continue;
            int? code = values[1] switch
            {
                int i => i,
                uint u => unchecked((int)u),
                long l => unchecked((int)l),
                _ => null,
            };
            events.Add(new HistoryEvent(record.Id, time, activity, task, code, values[2] as string));
        }
        return RunHistory.Build(events).Take(maxRuns).ToList();
    }

    public void Delete(string path)
    {
        using var task = Find(path) ?? throw new InvalidOperationException("The task no longer exists.");
        using var folder = task.Folder;
        folder.DeleteTask(task.Name, exceptionOnNotExists: false);
    }

    public void Run(string path)
    {
        using var task = Find(path) ?? throw new InvalidOperationException("The task no longer exists.");
        task.Run();
    }

    public void Stop(string path)
    {
        using var task = Find(path) ?? throw new InvalidOperationException("The task no longer exists.");
        task.Stop();
    }

    public void SetEnabled(string path, bool enabled)
    {
        using var task = Find(path) ?? throw new InvalidOperationException("The task no longer exists.");
        task.Enabled = enabled;
    }

    WinTask? Find(string path)
    {
        try
        {
            return Service.GetTask(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Every task, folder by folder, hidden ones included. Folders this account can't open are skipped.</summary>
    IEnumerable<WinTask> AllTasks(bool includeWindowsTasks = true)
    {
        var pending = new Stack<TaskFolder>();
        pending.Push(Service.RootFolder);
        while (pending.Count > 0)
        {
            using var folder = pending.Pop();
            List<WinTask> tasks;
            try
            {
                tasks = folder.GetTasks().ToList();
                foreach (var sub in folder.SubFolders)
                {
                    if (!includeWindowsTasks && string.Equals(sub.Path, @"\Microsoft", StringComparison.OrdinalIgnoreCase))
                        sub.Dispose();
                    else
                        pending.Push(sub);
                }
            }
            catch (Exception e) when (e is UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            {
                continue;
            }
            foreach (var task in tasks)
                yield return task;
        }
    }

    static TaskSnapshot Map(WinTask task)
    {
        var definition = task.Definition;
        var principal = definition.Principal;
        var settings = definition.Settings;
        var lastRun = Time(task.LastRunTime);
        return new TaskSnapshot
        {
            Path = task.Path,
            Description = Blank(definition.RegistrationInfo.Description),
            Author = Blank(definition.RegistrationInfo.Author),
            Created = Time(definition.RegistrationInfo.Date),
            State = MapState(task.State),
            Enabled = task.Enabled,
            LastRun = lastRun,
            LastResult = lastRun is null ? null : task.LastTaskResult,
            NextRun = Time(task.NextRunTime),
            RunAs = Blank(principal.UserId) ?? Blank(principal.GroupId) ?? "",
            Logon = principal.LogonType switch
            {
                TaskLogonType.InteractiveToken => LogonKind.Interactive,
                TaskLogonType.Password => LogonKind.Password,
                TaskLogonType.S4U => LogonKind.S4U,
                TaskLogonType.ServiceAccount => LogonKind.ServiceAccount,
                TaskLogonType.Group => LogonKind.Group,
                TaskLogonType.InteractiveTokenOrPassword => LogonKind.InteractiveOrPassword,
                _ => LogonKind.Unknown,
            },
            HighestPrivileges = principal.RunLevel == TaskRunLevel.Highest,
            Triggers = definition.Triggers.Select(MapTrigger).ToList(),
            Actions = definition.Actions.Select(MapAction).ToList(),
            Settings = new TaskSettingsInfo
            {
                StopAfter = settings.ExecutionTimeLimit > TimeSpan.Zero ? settings.ExecutionTimeLimit : null,
                RunMissedStart = settings.StartWhenAvailable,
                RestartOnFailure = settings.RestartCount > 0,
                RestartInterval = settings.RestartInterval,
                RestartCount = settings.RestartCount,
                MultipleInstances = settings.MultipleInstances.ToString(),
                OnlyOnAcPower = settings.DisallowStartIfOnBatteries,
                OnlyWhenIdle = settings.RunOnlyIfIdle,
                OnlyWithNetwork = settings.RunOnlyIfNetworkAvailable,
                WakeToRun = settings.WakeToRun,
                Hidden = settings.Hidden,
                AllowRunOnDemand = settings.AllowDemandStart,
            },
        };
    }

    static RunState MapState(TaskState state) => state switch
    {
        TaskState.Disabled => RunState.Disabled,
        TaskState.Queued => RunState.Queued,
        TaskState.Ready => RunState.Ready,
        TaskState.Running => RunState.Running,
        _ => RunState.Unknown,
    };

    internal static TriggerSpec MapTrigger(Trigger trigger)
    {
        var spec = new TriggerSpec
        {
            Enabled = trigger.Enabled,
            Start = trigger.StartBoundary == DateTime.MinValue ? null : trigger.StartBoundary,
            End = trigger.EndBoundary == DateTime.MaxValue ? null : trigger.EndBoundary,
            RepeatEvery = trigger.Repetition.Interval > TimeSpan.Zero ? trigger.Repetition.Interval : null,
            RepeatFor = trigger.Repetition.Duration > TimeSpan.Zero ? trigger.Repetition.Duration : null,
        };
        return trigger switch
        {
            TimeTrigger t => spec with { Kind = TriggerKind.Once, RandomDelay = Span(t.RandomDelay) },
            DailyTrigger t => spec with { Kind = TriggerKind.Daily, Interval = t.DaysInterval, RandomDelay = Span(t.RandomDelay) },
            WeeklyTrigger t => spec with { Kind = TriggerKind.Weekly, Interval = t.WeeksInterval, Days = (Weekdays)(int)t.DaysOfWeek, RandomDelay = Span(t.RandomDelay) },
            MonthlyTrigger t => spec with
            {
                Kind = TriggerKind.Monthly,
                Months = (MonthSet)(int)t.MonthsOfYear,
                DaysOfMonth = t.DaysOfMonth.Where(d => d is >= 1 and <= 31).Aggregate(0u, (bits, d) => bits | (1u << (d - 1))),
                LastDayOfMonth = t.RunOnLastDayOfMonth,
                RandomDelay = Span(t.RandomDelay),
            },
            MonthlyDOWTrigger t => spec with
            {
                Kind = TriggerKind.MonthlyDayOfWeek,
                Months = (MonthSet)(int)t.MonthsOfYear,
                Days = (Weekdays)(int)t.DaysOfWeek,
                Weeks = (WeekOfMonth)(int)t.WeeksOfMonth | (t.RunOnLastWeekOfMonth ? WeekOfMonth.Last : WeekOfMonth.None),
                RandomDelay = Span(t.RandomDelay),
            },
            LogonTrigger t => spec with { Kind = TriggerKind.AtLogOn, UserId = Blank(t.UserId), Delay = Span(t.Delay) },
            BootTrigger t => spec with { Kind = TriggerKind.AtStartup, Delay = Span(t.Delay) },
            IdleTrigger => spec with { Kind = TriggerKind.OnIdle },
            RegistrationTrigger t => spec with { Kind = TriggerKind.OnRegistration, Delay = Span(t.Delay) },
            SessionStateChangeTrigger t => spec with { Kind = TriggerKind.SessionStateChange, UserId = Blank(t.UserId), SessionChange = t.StateChange.ToString(), Delay = Span(t.Delay) },
            EventTrigger t => EventSpec(spec, t),
            _ => spec with { Kind = TriggerKind.Custom },
        };
    }

    static TriggerSpec EventSpec(TriggerSpec spec, EventTrigger t)
    {
        spec = spec with { Kind = TriggerKind.OnEvent, Delay = Span(t.Delay) };
        try
        {
            if (t.GetBasic(out var log, out var source, out var id))
                return spec with { EventLog = log, EventSource = Blank(source), EventId = id };
        }
        catch (Exception)
        {
            // A query too complex to summarise: described generically.
        }
        return spec;
    }

    static ActionInfo MapAction(Microsoft.Win32.TaskScheduler.Action action) => action switch
    {
        ExecAction e => new ActionInfo(ActionKind.Program, e.Path ?? "", Blank(e.Arguments), Blank(e.WorkingDirectory)),
        ComHandlerAction c => new ActionInfo(ActionKind.ComHandler, c.ClassName ?? c.ClassId.ToString("B"), Blank(c.Data)),
        EmailAction m => new ActionInfo(ActionKind.Email, m.To ?? ""),
        ShowMessageAction s => new ActionInfo(ActionKind.Message, s.Title ?? ""),
        _ => new ActionInfo(ActionKind.Program, action.ToString()),
    };

    static TimeSpan? Span(TimeSpan span) => span > TimeSpan.Zero ? span : null;

    /// <summary>Task Scheduler says "never" with dates near year 1 or 1899.</summary>
    static DateTime? Time(DateTime value) => value.Year < 1971 || value == DateTime.MaxValue ? null : value;

    static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;

    public void Dispose()
    {
        lock (_watchGate)
        {
            _watcher?.Dispose();
            _watcher = null;
        }
        foreach (var service in _service.Values)
            service.Dispose();
        _service.Dispose();
    }
}
