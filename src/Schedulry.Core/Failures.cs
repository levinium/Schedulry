namespace Schedulry.Core;

/// <summary>A run that has just failed, for a notification.</summary>
public sealed record FailureNotice(string Computer, bool IsRemote, TaskSnapshot Task)
{
    public ResultText Result => ResultCodes.Describe(Task.LastResult ?? 0);
    public string Title => $"{Task.Name} failed";

    /// <summary>"Program not found · 10:02 AM", with the computer's name when it isn't this one.</summary>
    public string Body
    {
        get
        {
            var when = Task.LastRun is { } run ? $" · {run:t}" : "";
            var where = IsRemote ? $" on {Computer}" : "";
            return $"{Result.Short}{when}{where}";
        }
    }
}

/// <summary>
/// Spots runs that have just failed, from the monitor's changes. Each run is reported once, and
/// nothing that had failed before the app started counts: only a new result does. A task's run is
/// told apart by its last-run time and result, since Task Scheduler writes the time when the run
/// starts and the result when it ends.
/// </summary>
public sealed class FailureWatch
{
    readonly Dictionary<string, (DateTime? LastRun, int? Result)> _known = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The state when watching began: none of it is new.</summary>
    public void Seed(IEnumerable<TaskSnapshot> tasks)
    {
        _known.Clear();
        foreach (var task in tasks)
            _known[task.Path] = (task.LastRun, task.LastResult);
    }

    /// <summary>The tasks among <paramref name="changes"/> whose latest run has just failed and that <paramref name="wanted"/> accepts.</summary>
    public IReadOnlyList<TaskSnapshot> Check(IReadOnlyList<TaskChange> changes, Func<TaskSnapshot, bool> wanted)
    {
        List<TaskSnapshot>? failed = null;
        foreach (var change in changes)
        {
            if (change.Task is not { } task)
            {
                _known.Remove(change.Path);
                continue;
            }
            var key = (task.LastRun, task.LastResult);
            var seenBefore = _known.TryGetValue(task.Path, out var known);
            _known[task.Path] = key;
            // A task that has only just appeared brings its history with it; that run isn't new.
            if (change.Kind == ChangeKind.Added || !seenBefore || known == key)
                continue;
            if (task.Outcome == TaskOutcome.Failed && wanted(task))
                (failed ??= []).Add(task);
        }
        return failed ?? [];
    }
}

/// <summary>Settings for the whole app, kept in settings.json.</summary>
public sealed class AppSettings
{
    /// <summary>A notification when a task's run fails.</summary>
    public bool NotifyFailures { get; set; } = true;
    /// <summary>Also for Windows' own tasks, which fail often and harmlessly.</summary>
    public bool NotifyWindowsTasks { get; set; }
    /// <summary>Closing the window leaves the app watching from the notification area.</summary>
    public bool KeepRunningWhenClosed { get; set; } = true;
    /// <summary>The "still running in the notification area" note has been shown once.</summary>
    public bool TrayNoteShown { get; set; }
}

/// <summary>Starting with Windows: the Windows one is an entry in the person's Run key.</summary>
public interface IStartup
{
    bool IsOn { get; }
    void Set(bool on);
}

/// <summary>
/// Whether other computers may manage this one's tasks: on Windows, the firewall rule groups for
/// remote task management and the remote event log. Changing it needs administrator approval.
/// </summary>
public interface IRemoteAccess
{
    /// <summary>Read fresh each time: someone may change the firewall outside the app.</summary>
    bool IsOn { get; }

    /// <summary>False when the person declined the administrator prompt.</summary>
    Task<bool> SetAsync(bool on);

    /// <summary>
    /// This PC's own administrator accounts (.\name) keep their administrator rights when they sign
    /// in from another computer. Windows strips them by default (UAC remote restrictions); domain
    /// accounts aren't affected. Weaker security, so it's a separate choice.
    /// </summary>
    bool LocalAdminsOn { get; }

    /// <summary>False when the person declined the administrator prompt.</summary>
    Task<bool> SetLocalAdminsAsync(bool on);
}

public interface ISettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);
}

public sealed class MemorySettingsStore(AppSettings? settings = null) : ISettingsStore
{
    AppSettings _settings = settings ?? new AppSettings();

    public AppSettings Load() => _settings;
    public void Save(AppSettings settings) => _settings = settings;
}

public sealed class JsonSettingsStore(string file) : ISettingsStore
{
    public AppSettings Load() => JsonFile.Load<AppSettings>(file) ?? new AppSettings();
    public void Save(AppSettings settings) => JsonFile.Save(file, settings);
}
