namespace Schedulry.Core;

public enum ChangeKind
{
    Added,
    Updated,
    Removed,
}

/// <summary>One task that appeared, changed or went away. <see cref="Task"/> is null for Removed.</summary>
public sealed record TaskChange(ChangeKind Kind, string Path, TaskSnapshot? Task);

/// <summary>How often the monitor looks. One tick is the smallest step; the others are counted in ticks.</summary>
public sealed record MonitorTiming
{
    /// <summary>
    /// Asking which tasks are running costs the Task Scheduler service about 130 ms on a typical
    /// computer (measured), so every 2 s rather than every second.
    /// </summary>
    public TimeSpan Tick { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>Re-read every task's state, times and result.</summary>
    public int StatusEveryTicks { get; init; } = 3;
    /// <summary>Same, while the history log reports changes as they happen.</summary>
    public int StatusEveryTicksWithHistory { get; init; } = 15;
    /// <summary>Windows' own tasks (\Microsoft) are swept this many times less often.</summary>
    public int WindowsTasksSlower { get; init; } = 6;
    /// <summary>Re-read every task in full, to catch definitions changed elsewhere.</summary>
    public int FullEveryTicks { get; init; } = 30;
    public int FullEveryTicksWithHistory { get; init; } = 150;

    /// <summary>
    /// Another computer: every call crosses the network, so ask half as often. Starts and finishes
    /// still show within a few seconds; with its history log readable they show at once.
    /// </summary>
    public static MonitorTiming Remote { get; } = new()
    {
        Tick = TimeSpan.FromSeconds(3),
        StatusEveryTicks = 4,
        StatusEveryTicksWithHistory = 20,
        FullEveryTicks = 40,
        FullEveryTicksWithHistory = 200,
    };
}

/// <summary>
/// Keeps a live copy of every task on one computer without anyone pressing Refresh.
/// <list type="bullet">
/// <item>Every tick it asks which instances are running (cheap), so a start or finish shows within a second.</item>
/// <item>Every few ticks it re-reads the state, times and result of all tasks.</item>
/// <item>Now and then it re-reads everything, to catch tasks added, changed or deleted elsewhere.</item>
/// <item>When the history log is on, its events re-read the task they name at once.</item>
/// </list>
/// Events are raised on the monitor's own thread; the UI marshals them.
/// </summary>
public sealed class LiveMonitor : IDisposable
{
    readonly ISchedulerHost _host;
    readonly MonitorTiming _timing;
    readonly Dictionary<string, TaskSnapshot> _tasks = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, RunningInstance> _running = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _pokedPaths = new(StringComparer.OrdinalIgnoreCase);
    readonly AutoResetEvent _wake = new(false);
    readonly object _gate = new();
    Thread? _thread;
    volatile bool _stopping;
    bool _loaded;
    bool _pokeAll;
    long _tick;

    public LiveMonitor(ISchedulerHost host, MonitorTiming? timing = null)
    {
        _host = host;
        _timing = timing ?? new MonitorTiming();
        _host.TaskEvent += OnHostEvent;
    }

    /// <summary>The first full read. Raised once.</summary>
    public event Action<IReadOnlyList<TaskSnapshot>>? Loaded;

    /// <summary>What changed since the last tick, in one batch.</summary>
    public event Action<IReadOnlyList<TaskChange>>? Changed;

    /// <summary>A read failed; the monitor keeps going and tries again next tick.</summary>
    public event Action<Exception>? Failed;

    /// <summary>Raised after every tick, changes or not (drives "running for" timers and "checked just now").</summary>
    public event Action<DateTime>? Ticked;

    public void Start()
    {
        if (_thread is not null)
            return;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Schedulry live monitor" };
        _thread.Start();
    }

    /// <summary>Look again now rather than at the next tick: after this app runs or changes a task.</summary>
    public void Poke(string? path = null)
    {
        lock (_gate)
        {
            if (path is null)
                _pokeAll = true;
            else
                _pokedPaths.Add(path);
        }
        _wake.Set();
    }

    void OnHostEvent(string path) => Poke(path);

    void Loop()
    {
        while (!_stopping)
        {
            try
            {
                Step();
            }
            catch (Exception e)
            {
                Failed?.Invoke(e);
            }
            _wake.WaitOne(_timing.Tick);
        }
    }

    /// <summary>One tick. Public for tests, which drive the monitor without its thread.</summary>
    public void Step()
    {
        if (!_loaded)
        {
            var all = _host.ReadAll();
            foreach (var task in all)
                _tasks[task.Path] = task;
            OverlayRunning(_host.ReadRunning(), []);
            _loaded = true;
            Loaded?.Invoke(_tasks.Values.ToList());
            Ticked?.Invoke(DateTime.Now);
            return;
        }

        _tick++;
        var changes = new Dictionary<string, TaskChange>(StringComparer.OrdinalIgnoreCase);
        var history = _host.HistoryEnabled;

        HashSet<string> poked;
        bool pokeAll;
        lock (_gate)
        {
            poked = new HashSet<string>(_pokedPaths, StringComparer.OrdinalIgnoreCase);
            _pokedPaths.Clear();
            pokeAll = _pokeAll;
            _pokeAll = false;
        }

        // 1. Running instances: a start or finish re-reads that task straight away.
        var running = _host.ReadRunning();
        var nowRunning = running.Select(r => r.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in nowRunning.Where(p => !_running.ContainsKey(p)))
            poked.Add(path);
        foreach (var path in _running.Keys.Where(p => !nowRunning.Contains(p)))
            poked.Add(path);

        // 2. The occasional full read catches definitions changed outside this app.
        var fullEvery = history ? _timing.FullEveryTicksWithHistory : _timing.FullEveryTicks;
        if (pokeAll || _tick % fullEvery == 0)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var task in _host.ReadAll())
            {
                seen.Add(task.Path);
                Apply(task, changes);
            }
            foreach (var gone in _tasks.Keys.Where(p => !seen.Contains(p)).ToList())
                Remove(gone, changes);
            poked.Clear();
        }
        else
        {
            // 3. The status sweep: state, times and result of every task.
            var statusEvery = history ? _timing.StatusEveryTicksWithHistory : _timing.StatusEveryTicks;
            if (_tick % statusEvery == 0)
            {
                // Windows' own tasks are many and rarely interesting: swept less often.
                var withWindows = _tick % (statusEvery * _timing.WindowsTasksSlower) == 0;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var status in _host.ReadStatus(withWindows))
                {
                    seen.Add(status.Path);
                    if (!_tasks.TryGetValue(status.Path, out var known))
                    {
                        poked.Add(status.Path);   // new: read it in full below
                        continue;
                    }
                    var moved = known.Enabled != status.Enabled || known.LastRun != status.LastRun || known.LastResult != status.LastResult
                        || (known.State != status.State && !(known.State == RunState.Running && _running.ContainsKey(status.Path)));
                    // Something moved: a full read brings the next run time (and anything else) up to date.
                    if (moved)
                        poked.Add(status.Path);
                    else if (status.NextRun is not null && status.NextRun != known.NextRun)
                        Apply(known with { NextRun = status.NextRun }, changes);
                }
                foreach (var gone in _tasks.Values.Where(t => (withWindows || !t.IsWindowsTask) && !seen.Contains(t.Path) && !poked.Contains(t.Path)).Select(t => t.Path).ToList())
                    Remove(gone, changes);
            }

            // 4. Single tasks that something pointed at.
            foreach (var path in poked)
            {
                if (_host.Read(path) is { } task)
                    Apply(task, changes);
                else
                    Remove(path, changes);
            }
        }

        OverlayRunning(running, changes);

        if (changes.Count > 0)
            Changed?.Invoke(changes.Values.ToList());
        Ticked?.Invoke(DateTime.Now);
    }

    /// <summary>The running list is the freshest source: it sets Running, the start time and the program.</summary>
    void OverlayRunning(IReadOnlyList<RunningInstance> running, Dictionary<string, TaskChange> changes)
    {
        _running.Clear();
        foreach (var instance in running)
            _running[instance.Path] = instance;

        foreach (var task in _tasks.Values.ToList())
        {
            TaskSnapshot updated;
            if (_running.TryGetValue(task.Path, out var instance))
                updated = task with { State = RunState.Running, RunningSince = instance.Since ?? task.RunningSince, CurrentAction = instance.CurrentAction };
            else if (task.State == RunState.Running || task.RunningSince is not null)
                updated = task with
                {
                    State = task.State == RunState.Running ? (task.Enabled ? RunState.Ready : RunState.Disabled) : task.State,
                    RunningSince = null,
                    CurrentAction = null,
                };
            else
                continue;
            if (changes is { } && !LiveEquals(task, updated))
                Apply(updated, changes);
        }
    }

    void Apply(TaskSnapshot task, Dictionary<string, TaskChange> changes)
    {
        if (_tasks.TryGetValue(task.Path, out var old))
        {
            // A sweep doesn't know about the running overlay; keep it until the running list says otherwise.
            if (_running.TryGetValue(task.Path, out var instance))
                task = task with { State = RunState.Running, RunningSince = instance.Since ?? old.RunningSince, CurrentAction = instance.CurrentAction };
            if (old.SameDefinition(task) && LiveEquals(old, task))
                return;
            _tasks[task.Path] = task;
            var kind = changes.TryGetValue(task.Path, out var earlier) && earlier.Kind == ChangeKind.Added ? ChangeKind.Added : ChangeKind.Updated;
            changes[task.Path] = new TaskChange(kind, task.Path, task);
        }
        else
        {
            _tasks[task.Path] = task;
            changes[task.Path] = new TaskChange(ChangeKind.Added, task.Path, task);
        }
    }

    void Remove(string path, Dictionary<string, TaskChange> changes)
    {
        if (_tasks.Remove(path))
            changes[path] = new TaskChange(ChangeKind.Removed, path, null);
    }

    static bool LiveEquals(TaskSnapshot a, TaskSnapshot b) =>
        a.State == b.State && a.Enabled == b.Enabled && a.LastRun == b.LastRun && a.LastResult == b.LastResult
        && a.NextRun == b.NextRun && a.RunningSince == b.RunningSince && a.CurrentAction == b.CurrentAction;

    public void Dispose()
    {
        _stopping = true;
        _host.TaskEvent -= OnHostEvent;
        _wake.Set();
        _thread?.Join(TimeSpan.FromSeconds(3));
        _wake.Dispose();
    }
}
