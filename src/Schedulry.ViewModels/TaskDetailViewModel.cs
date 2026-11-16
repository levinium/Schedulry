using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Schedulry.Core;

namespace Schedulry.ViewModels;

/// <summary>A labelled line in the preview: "Stop after" / "2 hours".</summary>
public sealed record DetailLine(string Label, string Value);

/// <summary>
/// The preview pane for the selected task: everything about it in plain words, with the buttons
/// that act on it. Follows the row, so it stays live too.
/// </summary>
public sealed partial class TaskDetailViewModel : ObservableObject, IDisposable
{
    readonly MainViewModel _shell;

    public TaskDetailViewModel(TaskRowViewModel row, MainViewModel shell)
    {
        Row = row;
        _shell = shell;
        row.PropertyChanged += OnRowChanged;
        Rebuild();
        RefreshGroups();
        _ = LoadNextRunsAsync();
        _ = LoadRunsAsync();
    }

    public TaskRowViewModel Row { get; }
    TaskSnapshot Task => Row.Task;

    [ObservableProperty] public partial string ResultTitle { get; private set; } = "";
    [ObservableProperty] public partial string? ResultDetail { get; private set; }
    [ObservableProperty] public partial string ResultCode { get; private set; } = "";
    [ObservableProperty] public partial string LastRunFull { get; private set; } = "";
    [ObservableProperty] public partial IReadOnlyList<string> NextRuns { get; private set; } = [];
    [ObservableProperty] public partial bool NextRunsLoading { get; private set; } = true;
    [ObservableProperty] public partial IReadOnlyList<string> Triggers { get; private set; } = [];
    [ObservableProperty] public partial IReadOnlyList<DetailLine> Actions { get; private set; } = [];
    [ObservableProperty] public partial IReadOnlyList<DetailLine> Settings { get; private set; } = [];
    [ObservableProperty] public partial string RunAsTitle { get; private set; } = "";
    [ObservableProperty] public partial string RunAsExplanation { get; private set; } = "";
    [ObservableProperty] public partial string? ActionNote { get; private set; }
    [ObservableProperty] public partial string? Problem { get; private set; }
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(EndCommand), nameof(TurnOnCommand), nameof(TurnOffCommand), nameof(AskDeleteCommand))]
    public partial bool Busy { get; private set; }

    // ---- Run history ----

    [ObservableProperty] public partial IReadOnlyList<RunLine> Runs { get; private set; } = [];
    [ObservableProperty] public partial bool RunsLoading { get; private set; } = true;
    public bool HistoryOff => !_shell.HistoryEnabled;
    public bool NoRuns => !RunsLoading && !HistoryOff && Runs.Count == 0;

    partial void OnRunsChanged(IReadOnlyList<RunLine> value) => OnPropertyChanged(nameof(NoRuns));
    partial void OnRunsLoadingChanged(bool value) => OnPropertyChanged(nameof(NoRuns));

    /// <summary>The last 30 days of runs from the history log (up to 25).</summary>
    public async Task LoadRunsAsync()
    {
        OnPropertyChanged(nameof(HistoryOff));
        if (HistoryOff)
        {
            Runs = [];
            RunsLoading = false;
            return;
        }
        var path = Task.Path;
        RunsLoading = Runs.Count == 0;
        IReadOnlyList<RunRecord> runs = [];
        try
        {
            await Platform.Background(() =>
            {
                runs = _shell.Host.ReadRuns(path, DateTime.Now.AddDays(-30), 25);
                return System.Threading.Tasks.Task.CompletedTask;
            });
        }
        catch (Exception)
        {
            // An unreadable log (permissions, a remote computer gone) shows as an empty history.
        }
        if (path != Task.Path)
            return;
        var now = DateTime.Now;
        Runs = runs.Select(r => RunLine.From(r, now)).ToList();
        RunsLoading = false;
    }

    [RelayCommand]
    Task TurnOnHistoryAsync() => _shell.TurnOnHistoryCommand.ExecuteAsync(null);

    // ---- Delete ----

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(AskDeleteCommand))] public partial bool IsConfirmingDelete { get; private set; }

    bool CanDelete() => !Busy && CanChange && !IsConfirmingDelete;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    void AskDelete() => IsConfirmingDelete = true;

    [RelayCommand]
    void CancelDelete() => IsConfirmingDelete = false;

    [RelayCommand]
    async Task ConfirmDeleteAsync()
    {
        IsConfirmingDelete = false;
        await ActAsync(host => host.Delete(Task.Path), "delete");
    }

    /// <summary>Saves the task's definition as Task Scheduler XML, so it can be imported again.</summary>
    [RelayCommand]
    async Task SaveCopyAsync()
    {
        Problem = null;
        try
        {
            var xml = "";
            await Platform.Background(() =>
            {
                xml = _shell.Host.ReadXml(Task.Path);
                return System.Threading.Tasks.Task.CompletedTask;
            });
            await Platform.SaveTextFile(Task.Name + ".xml", xml);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Problem = $"Couldn't save a copy: {e.Message}";
        }
    }

    // ---- Stars and groups (this app's own; Task Scheduler isn't touched) ----

    [ObservableProperty] public partial IReadOnlyList<CollectionItem> Groups { get; private set; } = [];
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(AddToGroupCommand))] public partial string GroupNameText { get; set; } = "";
    public IReadOnlyList<string> GroupNames => _shell.GroupNames;
    public bool HasGroups => Groups.Count > 0;
    public string StarText => Row.IsStarred ? "Starred" : "Star";

    partial void OnGroupsChanged(IReadOnlyList<CollectionItem> value) => OnPropertyChanged(nameof(HasGroups));

    [RelayCommand]
    void ToggleStar()
    {
        _shell.ToggleStar(Row);
        OnPropertyChanged(nameof(StarText));
    }

    bool CanAddToGroup() => GroupNameText.Trim().Length > 0;

    /// <summary>Adds to the named group, making it when the name is new.</summary>
    [RelayCommand(CanExecute = nameof(CanAddToGroup))]
    void AddToGroup()
    {
        _shell.AddToGroup(Row, GroupNameText);
        GroupNameText = "";
    }

    [RelayCommand]
    void RemoveFromGroup(CollectionItem? item)
    {
        if (item?.Group is { } group)
            _shell.RemoveFromGroup(Row, group);
    }

    public void RefreshGroups()
    {
        Groups = _shell.GroupsOf(Row.Path);
        OnPropertyChanged(nameof(GroupNames));
        OnPropertyChanged(nameof(StarText));
    }

    public string? Description => Task.Description;
    public bool HasDescription => !string.IsNullOrWhiteSpace(Task.Description);
    public bool HasProblem => Problem is not null;
    public bool HasActionNote => ActionNote is not null;
    public bool HasResultDetail => ResultDetail is not null;
    public bool NoNextRuns => !NextRunsLoading && NextRuns.Count == 0;

    partial void OnProblemChanged(string? value) => OnPropertyChanged(nameof(HasProblem));
    partial void OnActionNoteChanged(string? value) => OnPropertyChanged(nameof(HasActionNote));
    partial void OnResultDetailChanged(string? value) => OnPropertyChanged(nameof(HasResultDetail));
    partial void OnNextRunsChanged(IReadOnlyList<string> value) => OnPropertyChanged(nameof(NoNextRuns));
    partial void OnNextRunsLoadingChanged(bool value) => OnPropertyChanged(nameof(NoNextRuns));

    /// <summary>Changing a task needs admin rights, unless it runs as the person using this app.</summary>
    bool CanChange => _shell.IsElevated || string.Equals(Task.RunAs, _shell.CurrentUser, StringComparison.OrdinalIgnoreCase);

    bool CanRun() => !Busy && CanChange && Task.Enabled && Task.State != RunState.Running && Task.Settings.AllowRunOnDemand;
    bool CanEnd() => !Busy && CanChange && Task.State == RunState.Running;
    bool CanTurnOn() => !Busy && CanChange && !Task.Enabled;
    bool CanTurnOff() => !Busy && CanChange && Task.Enabled;

    [RelayCommand(CanExecute = nameof(CanRun))]
    Task RunAsync() => ActAsync(host => host.Run(Task.Path), "start");

    [RelayCommand(CanExecute = nameof(CanEnd))]
    Task EndAsync() => ActAsync(host => host.Stop(Task.Path), "end");

    [RelayCommand(CanExecute = nameof(CanTurnOn))]
    Task TurnOnAsync() => ActAsync(host => host.SetEnabled(Task.Path, true), "turn on");

    [RelayCommand(CanExecute = nameof(CanTurnOff))]
    Task TurnOffAsync() => ActAsync(host => host.SetEnabled(Task.Path, false), "turn off");

    async Task ActAsync(Action<ISchedulerHost> act, string verb)
    {
        Busy = true;
        Problem = null;
        try
        {
            await Platform.Background(() =>
            {
                act(_shell.Host);
                return System.Threading.Tasks.Task.CompletedTask;
            });
            _shell.Monitor.Poke(Task.Path);
        }
        catch (UnauthorizedAccessException)
        {
            Problem = $"Windows refused to {verb} this task: it needs administrator rights.";
        }
        catch (Exception e)
        {
            Problem = $"Couldn't {verb} the task: {e.Message}";
        }
        finally
        {
            Busy = false;
        }
    }

    void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TaskRowViewModel.IsStarred))
            OnPropertyChanged(nameof(StarText));
        if (e.PropertyName != nameof(TaskRowViewModel.Task))
            return;
        Rebuild();
        RunCommand.NotifyCanExecuteChanged();
        EndCommand.NotifyCanExecuteChanged();
        TurnOnCommand.NotifyCanExecuteChanged();
        TurnOffCommand.NotifyCanExecuteChanged();
        AskDeleteCommand.NotifyCanExecuteChanged();
        _ = LoadNextRunsAsync();
        _ = LoadRunsAsync();
    }

    /// <summary>Re-reads elevation (after the window comes back from a restart prompt, say).</summary>
    public void RefreshPermissions()
    {
        Rebuild();
        RunCommand.NotifyCanExecuteChanged();
        EndCommand.NotifyCanExecuteChanged();
        TurnOnCommand.NotifyCanExecuteChanged();
        TurnOffCommand.NotifyCanExecuteChanged();
    }

    void Rebuild()
    {
        var task = Task;
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(HasDescription));

        if (task.State == RunState.Running)
        {
            ResultTitle = "Running now";
            ResultDetail = task.CurrentAction is { } action ? $"Running {action}" : null;
            ResultCode = "";
        }
        else if (task.LastResult is { } code && task.LastRun is not null)
        {
            var described = ResultCodes.Describe(code);
            ResultTitle = described.Title;
            ResultDetail = described.Detail;
            ResultCode = $"Result code {described.Code}";
        }
        else
        {
            ResultTitle = "Has not run yet";
            ResultDetail = null;
            ResultCode = "";
        }
        LastRunFull = task.LastRun is { } last ? $"{TriggerText.DateText(last)} at {last:T}" : "Never";

        Triggers = task.Triggers.Count == 0 ? ["Only when run by hand"] : task.Triggers.Select(t => t.Summary).ToList();
        Actions = task.Actions.Select(a => new DetailLine(a.Summary, a.WorkingDirectory is { } dir ? $"Starts in {dir}" : "")).ToList();

        (RunAsTitle, RunAsExplanation) = Account(task);

        var settings = new List<DetailLine>
        {
            new("Stop after", task.Settings.StopAfter is { } limit ? TriggerText.Duration(limit) : "Never stopped for running too long"),
            new("If already running", task.Settings.MultipleInstances switch
            {
                "Parallel" => "Start another copy alongside it",
                "Queue" => "Start the new run when the current one ends",
                "StopExisting" => "Stop the running copy, then start again",
                _ => "Don't start a new run",
            }),
            new("Missed a start", task.Settings.RunMissedStart ? "Run as soon as possible afterwards" : "Skip it and wait for the next one"),
        };
        if (task.Settings.RestartOnFailure)
            settings.Add(new("If it fails", $"Try again every {TriggerText.Duration(task.Settings.RestartInterval)}, up to {task.Settings.RestartCount} times"));
        var conditions = new List<string>();
        if (task.Settings.OnlyOnAcPower)
            conditions.Add("on mains power");
        if (task.Settings.OnlyWhenIdle)
            conditions.Add("when the computer is idle");
        if (task.Settings.OnlyWithNetwork)
            conditions.Add("with a network connection");
        if (conditions.Count > 0)
            settings.Add(new("Only runs", string.Join(", ", conditions)));
        if (task.Settings.WakeToRun)
            settings.Add(new("Sleep", "Wakes the computer to run"));
        if (task.Settings.Hidden)
            settings.Add(new("Hidden", "Hidden in the old Task Scheduler window"));
        if (task.Author is { } author)
            settings.Add(new("Created by", author));
        Settings = settings;

        ActionNote = CanChange ? null
            : _shell.IsElevated ? null
            : "Changing this task needs administrator rights, because it runs as another account.";
        if (CanChange && !task.Settings.AllowRunOnDemand)
            ActionNote = "This task is set to not allow running by hand.";
    }

    static (string Title, string Explanation) Account(TaskSnapshot task)
    {
        var who = string.IsNullOrEmpty(task.RunAs) ? "Unknown account" : task.RunAs;
        var how = task.Logon switch
        {
            LogonKind.Interactive => "Runs only while this person is signed in, on their desktop, where they can see it.",
            LogonKind.Password => "Runs whether or not anyone is signed in, using the stored password. It stops working if that password changes.",
            LogonKind.S4U => "Runs whether or not anyone is signed in, without a stored password. It can't reach network shares or encrypted files, and the account needs the \"Log on as a batch job\" right.",
            LogonKind.ServiceAccount => "Runs as a built-in Windows account, in the background, with no password to keep up to date.",
            LogonKind.Group => "Runs for any signed-in member of this group.",
            LogonKind.InteractiveOrPassword => "Runs on the desktop while this person is signed in, otherwise with the stored password.",
            _ => "Sign-in type not known.",
        };
        if (task.HighestPrivileges)
            how += " Runs with full administrator rights.";
        return (who, how);
    }

    async Task LoadNextRunsAsync()
    {
        var path = Task.Path;
        NextRunsLoading = true;
        IReadOnlyList<DateTime> times = [];
        try
        {
            await Platform.Background(() =>
            {
                times = _shell.Host.NextRuns(path, 5);
                return System.Threading.Tasks.Task.CompletedTask;
            });
        }
        catch (Exception)
        {
            // Windows can refuse this for tasks the account can't read; the list just stays empty.
        }
        if (path != Task.Path)
            return;
        var now = DateTime.Now;
        NextRuns = times.Select(t => When.At(t, now)).ToList();
        NextRunsLoading = false;
    }

    public void Dispose() => Row.PropertyChanged -= OnRowChanged;
}
