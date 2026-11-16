using System.Xml.Linq;
using Schedulry.Core;

namespace Schedulry.Demo;

/// <summary>
/// A pretend computer, CONTOSO-WS01. A few of its tasks run on short loops (every 20-90 seconds)
/// so the live view has something to show; the rest sit at believable times. With a fixed clock
/// it's frozen, for screenshots and tests.
/// </summary>
public sealed class DemoSchedulerHost : ISchedulerHost
{
    sealed class Entry
    {
        public required TaskSnapshot Task;
        /// <summary>Simulated loop: start every Cycle, run for Duration, fail every FailEvery-th run.</summary>
        public TimeSpan? Cycle;
        public TimeSpan Duration;
        public TimeSpan Offset;
        public int FailEvery;
        public int FailCode = 1;
        public (DateTime Start, DateTime End)? Manual;
        public DateTime? StoppedAt;
    }

    readonly Func<DateTime> _clock;
    readonly DateTime _epoch;
    readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    readonly object _gate = new();

    /// <param name="clock">Defaults to the real clock; pass a fixed one for a still picture.</param>
    public DemoSchedulerHost(Func<DateTime>? clock = null, bool elevated = false, bool historyEnabled = false)
    {
        _clock = clock ?? (() => DateTime.Now);
        var now = _clock();
        _epoch = now.Date;
        IsElevated = elevated;
        HistoryEnabled = historyEnabled;
        foreach (var entry in DemoTasks.Build(now))
            _entries[entry.Task.Path] = new Entry
            {
                Task = entry.Task,
                Cycle = entry.Cycle,
                Duration = entry.Duration,
                Offset = entry.Offset,
                FailEvery = entry.FailEvery,
                FailCode = entry.FailCode,
            };
    }

    public string ComputerName => "CONTOSO-WS01";
    public bool IsElevated { get; set; }
    public string CurrentUser => @"CONTOSO\avery.lee";
    public bool HistoryEnabled { get; set; }

    public bool RecheckHistory() => HistoryEnabled;

    public event Action<string>? TaskEvent
    {
        add { }
        remove { }
    }

    public IReadOnlyList<TaskSnapshot> ReadAll()
    {
        lock (_gate)
            return _entries.Values.Select(Current).ToList();
    }

    public TaskSnapshot? Read(string path)
    {
        lock (_gate)
            return _entries.TryGetValue(path, out var entry) ? Current(entry) : null;
    }

    public IReadOnlyList<TaskStatusInfo> ReadStatus(bool includeWindowsTasks = true)
    {
        lock (_gate)
            return _entries.Values.Select(Current).Where(t => includeWindowsTasks || !t.IsWindowsTask)
                .Select(t => new TaskStatusInfo(t.Path, t.State, t.Enabled, t.LastRun, t.LastResult, t.NextRun)).ToList();
    }

    public IReadOnlyList<RunningInstance> ReadRunning()
    {
        lock (_gate)
            return _entries.Values.Select(Current).Where(t => t.State == RunState.Running)
                .Select(t => new RunningInstance(t.Path, t.LastRun, t.Actions.FirstOrDefault()?.Path, 4000 + Math.Abs(t.Path.GetHashCode() % 3000))).ToList();
    }

    public IReadOnlyList<DateTime> NextRuns(string path, int count)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(path, out var entry) || !entry.Task.Enabled)
                return [];
            var now = _clock();
            if (entry.Cycle is { } cycle)
            {
                var (_, start) = CycleAt(entry, now);
                return Enumerable.Range(1, count).Select(i => start + cycle * i).ToList();
            }
            return DemoCalendar.Next(entry.Task.Triggers, now, count);
        }
    }

    public string ReadXml(string path)
    {
        var task = Read(path);
        return task is null ? "" : DemoXml.For(task);
    }

    public IReadOnlyList<RunRecord> ReadRuns(string? path, DateTime since, int maxRuns)
    {
        if (!HistoryEnabled)
            return [];
        lock (_gate)
        {
            var entries = path is null ? _entries.Values.ToList()
                : _entries.TryGetValue(path, out var one) ? [one] : [];
            var now = _clock();
            return entries.SelectMany(e => RunsOf(e, now, since, maxRuns))
                .OrderByDescending(r => r.Start).Take(maxRuns).ToList();
        }
    }

    /// <summary>A believable past for one demo task: its loop's runs, or a few at its usual spacing.</summary>
    IEnumerable<RunRecord> RunsOf(Entry entry, DateTime now, DateTime since, int max)
    {
        var task = entry.Task;
        var seed = Math.Abs(task.Path.GetHashCode());
        if (entry.Manual is { } manual && manual.Start >= since)
            yield return new RunRecord
            {
                Path = task.Path, ActivityId = Guid.NewGuid(), Start = manual.Start, Cause = RunCause.Person,
                End = now < manual.End ? null : manual.End,
                Outcome = now < manual.End ? RunEnd.Running : RunEnd.Succeeded, ResultCode = now < manual.End ? null : 0,
            };

        if (entry.Cycle is { } cycle && task.Enabled)
        {
            var (index, start) = CycleAt(entry, now);
            for (var i = 0; i < max && start >= since; i++, index--, start -= cycle)
            {
                var running = now - start < entry.Duration && !(entry.StoppedAt is { } at && at >= start);
                var failed = entry.FailEvery > 0 && index % entry.FailEvery == 0;
                yield return new RunRecord
                {
                    Path = task.Path, ActivityId = Guid.NewGuid(), Start = start, Cause = RunCause.Schedule,
                    End = running ? null : start + entry.Duration,
                    Outcome = running ? RunEnd.Running : failed ? RunEnd.Failed : RunEnd.Succeeded,
                    ResultCode = running ? null : failed ? entry.FailCode : 0,
                    Program = task.Actions.FirstOrDefault()?.Path,
                };
            }
            yield break;
        }

        if (task.LastRun is not { } last)
            yield break;
        var spacing = task.Triggers.FirstOrDefault()?.Kind switch
        {
            TriggerKind.Weekly => TimeSpan.FromDays(7),
            TriggerKind.Monthly or TriggerKind.MonthlyDayOfWeek => TimeSpan.FromDays(30),
            TriggerKind.AtLogOn or TriggerKind.AtStartup or TriggerKind.OnEvent => TimeSpan.FromDays(3),
            _ => TimeSpan.FromDays(1),
        };
        var cause = task.Triggers.FirstOrDefault()?.Kind switch
        {
            TriggerKind.AtLogOn => RunCause.LogOn,
            TriggerKind.AtStartup => RunCause.Startup,
            TriggerKind.OnEvent => RunCause.Event,
            _ => RunCause.Schedule,
        };
        for (var i = 0; i < Math.Min(max, 8); i++)
        {
            var start = last - spacing * i;
            if (start < since)
                yield break;
            var code = i == 0 ? task.LastResult ?? 0 : 0;
            var seconds = 4 + (seed + i * 37) % 80;
            yield return new RunRecord
            {
                Path = task.Path, ActivityId = Guid.NewGuid(), Start = start, End = start.AddSeconds(seconds), Cause = cause,
                Outcome = code == 0 ? RunEnd.Succeeded : code == 0x41306 ? RunEnd.Stopped : RunEnd.Failed, ResultCode = code,
                Program = task.Actions.FirstOrDefault()?.Path,
            };
        }
    }

    public void Delete(string path)
    {
        lock (_gate)
        {
            CheckCanChange(Get(path).Task);
            _entries.Remove(path);
        }
    }

    public void Run(string path)
    {
        lock (_gate)
        {
            var entry = Get(path);
            if (!entry.Task.Settings.AllowRunOnDemand)
                throw new InvalidOperationException("This task doesn't allow being run on demand.");
            var now = _clock();
            entry.Manual = (now, now + (entry.Duration > TimeSpan.Zero ? entry.Duration : TimeSpan.FromSeconds(6)));
            entry.StoppedAt = null;
        }
    }

    public void Stop(string path)
    {
        lock (_gate)
        {
            var entry = Get(path);
            entry.StoppedAt = _clock();
            entry.Manual = null;
        }
    }

    public void SetEnabled(string path, bool enabled)
    {
        lock (_gate)
        {
            var entry = Get(path);
            CheckCanChange(entry.Task);
            entry.Task = entry.Task with { Enabled = enabled };
        }
    }

    void CheckCanChange(TaskSnapshot task)
    {
        if (!IsElevated && !string.Equals(task.RunAs, CurrentUser, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Access is denied.");
    }

    Entry Get(string path) =>
        _entries.TryGetValue(path, out var entry) ? entry : throw new InvalidOperationException("The task no longer exists.");

    (long Index, DateTime Start) CycleAt(Entry entry, DateTime now)
    {
        var cycle = entry.Cycle!.Value;
        var index = (long)Math.Floor((now - _epoch + entry.Offset).Ticks / (double)cycle.Ticks);
        return (index, _epoch - entry.Offset + cycle * index);
    }

    /// <summary>The task as it is at this moment of the simulation.</summary>
    TaskSnapshot Current(Entry entry)
    {
        var task = entry.Task;
        var now = _clock();
        if (!task.Enabled)
            return task with { State = RunState.Disabled, NextRun = null, RunningSince = null };

        // Run from this app: running until its end, then a success.
        if (entry.Manual is { } manual)
        {
            if (now < manual.End)
                return task with { State = RunState.Running, LastRun = manual.Start, LastResult = 0x41301, RunningSince = manual.Start, CurrentAction = task.Actions.FirstOrDefault()?.Path };
            task = task with { LastRun = manual.Start, LastResult = 0 };
        }

        if (entry.Cycle is { } cycle)
        {
            var (index, start) = CycleAt(entry, now);
            var stopped = entry.StoppedAt is { } at && at >= start;
            var manualIsLater = entry.Manual is { } m && m.Start > start;
            if (manualIsLater)
                return task with { State = RunState.Ready, NextRun = start + cycle };
            if (now - start < entry.Duration && !stopped)
                return task with { State = RunState.Running, LastRun = start, LastResult = 0x41301, NextRun = start + cycle, RunningSince = start, CurrentAction = task.Actions.FirstOrDefault()?.Path };
            var failed = entry.FailEvery > 0 && index % entry.FailEvery == 0;
            var result = stopped ? 0x41306 : failed ? entry.FailCode : 0;
            return task with { State = RunState.Ready, LastRun = start, LastResult = result, NextRun = start + cycle };
        }

        if (entry.StoppedAt is not null && entry.Manual is null)
            task = task with { LastResult = 0x41306 };
        var next = DemoCalendar.Next(task.Triggers, now, 1);
        return task with { State = RunState.Ready, NextRun = next.Count > 0 ? next[0] : null };
    }

    public void Dispose()
    {
    }
}

/// <summary>Next run times for the demo's simple triggers (the real host asks Windows).</summary>
public static class DemoCalendar
{
    public static IReadOnlyList<DateTime> Next(IEnumerable<TriggerSpec> triggers, DateTime after, int count)
    {
        var times = new SortedSet<DateTime>();
        foreach (var trigger in triggers.Where(t => t.Enabled && t.Start is not null))
            foreach (var time in ForTrigger(trigger, after).Take(count))
                times.Add(time);
        return times.Take(count).ToList();
    }

    static IEnumerable<DateTime> ForTrigger(TriggerSpec t, DateTime after)
    {
        var start = t.Start!.Value;
        if (t.Kind == TriggerKind.Once)
        {
            foreach (var time in Repeats(t, start).Where(x => x > after))
                yield return time;
            yield break;
        }
        var day = after.Date.AddDays(-1);
        for (var i = 0; i < 800; i++, day = day.AddDays(1))
        {
            if (day < start.Date || !DayMatches(t, day, start))
                continue;
            foreach (var time in Repeats(t, day + start.TimeOfDay).Where(x => x > after))
                yield return time;
        }
    }

    static IEnumerable<DateTime> Repeats(TriggerSpec t, DateTime first)
    {
        yield return first;
        if (t.RepeatEvery is not { } every || every <= TimeSpan.Zero)
            yield break;
        var until = first + (t.RepeatFor ?? TimeSpan.FromDays(1) - TimeSpan.FromTicks(1));
        for (var time = first + every; time <= until; time += every)
            yield return time;
    }

    static bool DayMatches(TriggerSpec t, DateTime day, DateTime start)
    {
        var weekday = (Weekdays)(1 << (int)day.DayOfWeek);
        var monthOk = t.Months is MonthSet.None or MonthSet.All || ((int)t.Months & (1 << (day.Month - 1))) != 0;
        return t.Kind switch
        {
            TriggerKind.Daily => (day - start.Date).Days % Math.Max(1, t.Interval) == 0,
            TriggerKind.Weekly => t.Days.HasFlag(weekday) && ((day - start.Date).Days / 7) % Math.Max(1, t.Interval) == 0,
            TriggerKind.Monthly => monthOk && ((t.DaysOfMonth & (1u << (day.Day - 1))) != 0 || (t.LastDayOfMonth && day.AddDays(1).Month != day.Month)),
            TriggerKind.MonthlyDayOfWeek => monthOk && t.Days.HasFlag(weekday) && WeekMatches(t.Weeks, day),
            _ => false,
        };
    }

    static bool WeekMatches(WeekOfMonth weeks, DateTime day)
    {
        var nth = (day.Day - 1) / 7;
        var flag = nth switch { 0 => WeekOfMonth.First, 1 => WeekOfMonth.Second, 2 => WeekOfMonth.Third, 3 => WeekOfMonth.Fourth, _ => WeekOfMonth.None };
        var last = day.AddDays(7).Month != day.Month;
        return weeks.HasFlag(flag) && flag != WeekOfMonth.None || (weeks.HasFlag(WeekOfMonth.Last) && last);
    }
}

/// <summary>A believable Task Scheduler XML document for a demo task.</summary>
static class DemoXml
{
    static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    public static string For(TaskSnapshot task)
    {
        var doc = new XElement(Ns + "Task", new XAttribute("version", "1.4"),
            new XElement(Ns + "RegistrationInfo",
                task.Author is null ? null : new XElement(Ns + "Author", task.Author),
                task.Description is null ? null : new XElement(Ns + "Description", task.Description),
                new XElement(Ns + "URI", task.Path)),
            new XElement(Ns + "Triggers", task.Triggers.Select(Trigger)),
            new XElement(Ns + "Principals", new XElement(Ns + "Principal", new XAttribute("id", "Author"),
                new XElement(Ns + "UserId", task.RunAs),
                new XElement(Ns + "LogonType", task.Logon switch
                {
                    LogonKind.Password => "Password",
                    LogonKind.S4U => "S4U",
                    LogonKind.ServiceAccount => "ServiceAccount",
                    LogonKind.Group => "Group",
                    _ => "InteractiveToken",
                }),
                task.HighestPrivileges ? new XElement(Ns + "RunLevel", "HighestAvailable") : null)),
            new XElement(Ns + "Settings",
                new XElement(Ns + "MultipleInstancesPolicy", task.Settings.MultipleInstances),
                new XElement(Ns + "DisallowStartIfOnBatteries", task.Settings.OnlyOnAcPower ? "true" : "false"),
                new XElement(Ns + "StartWhenAvailable", task.Settings.RunMissedStart ? "true" : "false"),
                new XElement(Ns + "Enabled", task.Enabled ? "true" : "false"),
                new XElement(Ns + "ExecutionTimeLimit", task.Settings.StopAfter is { } limit ? System.Xml.XmlConvert.ToString(limit) : "PT0S")),
            new XElement(Ns + "Actions", new XAttribute("Context", "Author"), task.Actions.Select(a =>
                new XElement(Ns + "Exec",
                    new XElement(Ns + "Command", a.Path),
                    a.Arguments is null ? null : new XElement(Ns + "Arguments", a.Arguments),
                    a.WorkingDirectory is null ? null : new XElement(Ns + "WorkingDirectory", a.WorkingDirectory)))));
        return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>" + Environment.NewLine + doc;
    }

    static XElement Trigger(TriggerSpec t)
    {
        var name = t.Kind switch
        {
            TriggerKind.AtLogOn => "LogonTrigger",
            TriggerKind.AtStartup => "BootTrigger",
            TriggerKind.OnEvent => "EventTrigger",
            TriggerKind.OnIdle => "IdleTrigger",
            _ => "CalendarTrigger",
        };
        return new XElement(Ns + name,
            t.Start is { } s ? new XElement(Ns + "StartBoundary", s.ToString("yyyy-MM-ddTHH:mm:ss")) : null,
            new XElement(Ns + "Enabled", t.Enabled ? "true" : "false"),
            t.RepeatEvery is { } every ? new XElement(Ns + "Repetition", new XElement(Ns + "Interval", System.Xml.XmlConvert.ToString(every))) : null);
    }
}
