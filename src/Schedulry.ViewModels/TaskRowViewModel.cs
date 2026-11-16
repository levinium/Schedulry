using CommunityToolkit.Mvvm.ComponentModel;
using Schedulry.Core;

namespace Schedulry.ViewModels;

/// <summary>
/// One row of the task list. Updated in place when the task changes, so the row never jumps or
/// loses its selection. When a run starts or finishes, its status badge pulses briefly.
/// </summary>
public sealed partial class TaskRowViewModel : ObservableObject
{
    static readonly TimeSpan FlashFor = TimeSpan.FromSeconds(2.5);
    DateTime? _changedAt;

    public TaskRowViewModel(TaskSnapshot task, DateTime now)
    {
        Task = task;
        Refresh(now);
    }

    [ObservableProperty] public partial TaskSnapshot Task { get; private set; }
    [ObservableProperty] public partial string StatusText { get; private set; } = "";
    [ObservableProperty] public partial ChipKind StatusKind { get; private set; }
    [ObservableProperty] public partial bool IsRunning { get; private set; }
    [ObservableProperty] public partial string LastRunText { get; private set; } = "";
    [ObservableProperty] public partial string ResultText { get; private set; } = "";
    [ObservableProperty] public partial string ResultShort { get; private set; } = "";
    [ObservableProperty] public partial ChipKind ResultKind { get; private set; }
    [ObservableProperty] public partial string NextRunText { get; private set; } = "";
    /// <summary>True for a moment after a run starts or finishes (the badge pulses); nothing else sets it.</summary>
    [ObservableProperty] public partial bool IsFlashing { get; private set; }
    /// <summary>Starred by the person (kept by this app, not in Task Scheduler).</summary>
    [ObservableProperty] public partial bool IsStarred { get; set; }

    public string Path => Task.Path;
    public string Name => Task.Name;
    public string Folder => Task.Folder;

    /// <summary>The first trigger in words, and how many more there are.</summary>
    public string ScheduleText => Task.Triggers.Count switch
    {
        0 => "Only when run by hand",
        1 => Task.Triggers[0].Summary,
        var n => $"{Task.Triggers[0].Summary} (+{n - 1} more)",
    };

    public void Update(TaskSnapshot task, DateTime now)
    {
        var definitionChanged = !Task.SameDefinition(task);
        var runMoved = (Task.State == RunState.Running) != (task.State == RunState.Running) || Task.LastRun != task.LastRun;
        Task = task;
        if (runMoved)
        {
            _changedAt = now;
            IsFlashing = true;
        }
        if (definitionChanged)
        {
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(Folder));
            OnPropertyChanged(nameof(ScheduleText));
        }
        Refresh(now);
    }

    /// <summary>Recomputes the texts that depend on the clock ("Running 0:42", "in 3 min").</summary>
    public void Refresh(DateTime now)
    {
        var task = Task;
        var outcome = task.Outcome;
        IsRunning = task.State == RunState.Running;
        (StatusText, StatusKind) = task.State switch
        {
            // Just "Running": the time it's been going is in the Last Run column, where a long
            // run ("2d 4h") has room and the badge keeps one width.
            RunState.Running => ("Running", ChipKind.Info),
            RunState.Disabled => ("Turned off", ChipKind.Neutral),
            RunState.Queued => ("Queued", ChipKind.Warning),
            _ when !task.Enabled => ("Turned off", ChipKind.Neutral),
            _ => outcome switch
            {
                TaskOutcome.Failed => ("Failed", ChipKind.Danger),
                TaskOutcome.Warning => ("Check", ChipKind.Warning),
                TaskOutcome.NeverRun => ("Waiting", ChipKind.Neutral),
                _ => ("Ready", ChipKind.Success),
            },
        };

        LastRunText = IsRunning ? $"Running for {When.Elapsed(now - (task.RunningSince ?? task.LastRun ?? now))}" : When.Ago(task.LastRun, now);
        if (IsRunning)
            (ResultText, ResultShort, ResultKind) = ("Running now", "Running now", ChipKind.Info);
        else if (task.LastResult is { } code && task.LastRun is not null)
        {
            var described = ResultCodes.Describe(code);
            ResultText = described.Title;
            ResultShort = described.Short;
            ResultKind = described.Outcome switch
            {
                TaskOutcome.Succeeded => ChipKind.Success,
                TaskOutcome.Failed => ChipKind.Danger,
                TaskOutcome.Warning => ChipKind.Warning,
                TaskOutcome.Running => ChipKind.Info,
                _ => ChipKind.Neutral,
            };
        }
        else
            (ResultText, ResultShort, ResultKind) = ("Has not run yet", "Not run yet", ChipKind.Neutral);

        NextRunText = !task.Enabled ? "Turned off" : When.Until(task.NextRun, now);
        if (_changedAt is { } at && now - at > FlashFor)
        {
            IsFlashing = false;
            _changedAt = null;
        }
    }
}
