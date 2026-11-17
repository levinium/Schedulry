using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Schedulry.Core;

namespace Schedulry.ViewModels;

/// <summary>A sidebar entry that filters the list by status.</summary>
public sealed partial class ViewItem(string key, string title, Func<TaskSnapshot, bool> match) : ObservableObject
{
    public string Key { get; } = key;
    public string Title { get; } = title;
    public Func<TaskSnapshot, bool> Match { get; } = match;
    [ObservableProperty] public partial int Count { get; set; }
}

/// <summary>A sidebar folder. Depth indents it under its parent.</summary>
public sealed partial class FolderItem(string path, int depth) : ObservableObject
{
    public string Path { get; } = path;
    public int Depth { get; } = depth;
    public string Title => Path == "\\" ? "All folders" : Path[(Path.LastIndexOf('\\') + 1)..];
    public double Indent => 12 + Depth * 14;
    [ObservableProperty] public partial int Count { get; set; }
}

/// <summary>"Starred" (no group) or one of the person's own groups, in the sidebar's My Tasks section.</summary>
public sealed partial class CollectionItem : ObservableObject
{
    public CollectionItem(TaskGroup? group)
    {
        Group = group;
        Title = group?.Name ?? "Starred";
    }

    public TaskGroup? Group { get; }
    public bool IsStarredList => Group is null;
    public bool IsGroup => Group is not null;
    [ObservableProperty] public partial string Title { get; set; }
    [ObservableProperty] public partial int Count { get; set; }
}

public enum SortField
{
    Name,
    Status,
    LastRun,
    NextRun,
    Folder,
}

public sealed record SortOption(SortField Field, string Title);

/// <summary>
/// The main window: the sidebar, the live task list and the preview. Owns the monitor; every
/// change it reports updates rows in place on the UI thread.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    readonly Dictionary<string, TaskRowViewModel> _rows = new(StringComparer.OrdinalIgnoreCase);
    readonly Func<DateTime> _clock;
    readonly IOrganizerStore _store;
    readonly OrganizerData _organizer;

    public MainViewModel(ISchedulerHost host, MonitorTiming? timing = null, Func<DateTime>? clock = null, IOrganizerStore? organizer = null,
        IComputerStore? computers = null, IPasswordVault? passwords = null, SettingsViewModel? settings = null)
    {
        Host = host;
        Settings = settings ?? new SettingsViewModel(new MemorySettingsStore());
        _clock = clock ?? (() => DateTime.Now);
        _store = organizer ?? new MemoryOrganizerStore();
        _organizer = _store.Load();
        RebuildCollections();
        Monitor = new LiveMonitor(host, timing ?? (host.IsRemote ? MonitorTiming.Remote : null));
        IsElevated = host.IsElevated;
        CurrentUser = host.CurrentUser;
        ComputerName = host.ComputerName;
        Computers = new ComputersViewModel(this, computers ?? new MemoryComputerStore(), passwords ?? new MemoryPasswordVault());

        Views =
        [
            new("all", "All tasks", _ => true),
            new("running", "Running now", t => t.State == RunState.Running),
            new("failed", "Failed last run", t => t.Enabled && t.State != RunState.Running && t.Outcome == TaskOutcome.Failed),
            new("attention", "Needs a look", t => t.Enabled && t.State != RunState.Running && t.Outcome == TaskOutcome.Warning),
            new("off", "Turned off", t => !t.Enabled),
            new("never", "Never run", t => t.LastRun is null),
        ];
        SelectedView = Views[0];
        SortOptions =
        [
            new(SortField.Name, "Name"),
            new(SortField.Status, "Status"),
            new(SortField.LastRun, "Last run"),
            new(SortField.NextRun, "Next run"),
            new(SortField.Folder, "Folder"),
        ];
        SelectedSort = SortOptions[0];

        Monitor.Loaded += tasks => Platform.Post(() => OnLoaded(tasks));
        Monitor.Changed += changes => Platform.Post(() => OnChanged(changes));
        Monitor.Ticked += _ => Platform.Post(OnTicked);
        Monitor.Failed += e => Platform.Post(() => LiveProblem = $"Couldn't read Task Scheduler: {e.Message}");
        Settings.PropertyChanged += OnSettingsChanged;
    }

    void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Detail?.RefreshMuted();

    public ISchedulerHost Host { get; }
    public LiveMonitor Monitor { get; }
    public string ComputerName { get; }
    public string CurrentUser { get; }
    [ObservableProperty] public partial bool IsElevated { get; private set; }

    // ---- Computers ----

    public bool IsRemote => Host.IsRemote;
    public bool IsLocal => !Host.IsRemote;
    public ComputersViewModel Computers { get; }
    [ObservableProperty] public partial bool ShowComputers { get; set; }

    /// <summary>
    /// Raised once another computer has answered. The app replaces this window's view model with
    /// one for <c>host</c> and disposes this one; with no handler the host is closed again.
    /// </summary>
    public event Action<ISchedulerHost>? SwitchRequested;

    [RelayCommand]
    void ToggleComputers()
    {
        ShowComputers = !ShowComputers;
        if (ShowComputers)
            ShowSettings = false;
    }

    // ---- Settings and notifications ----

    public SettingsViewModel Settings { get; }
    public AppUpdate Update => AppUpdate.Current;
    [ObservableProperty] public partial bool ShowSettings { get; set; }
    readonly FailureWatch _failures = new();

    [RelayCommand]
    void ToggleSettings()
    {
        ShowSettings = !ShowSettings;
        if (ShowSettings)
        {
            ShowComputers = false;
            Settings.Refresh();
        }
    }

    /// <summary>The window is hidden in the notification area: the monitor looks less often.</summary>
    public bool InBackground
    {
        get => Monitor.InBackground;
        set => Monitor.InBackground = value;
    }

    public bool IsMuted(string path) => _organizer.IsMuted(path);

    /// <summary>Stops (or restarts) notifications for one task's failures.</summary>
    public void SetMuted(string path, bool muted)
    {
        _organizer.SetMuted(path, muted);
        Save();
        if (Detail is { } detail && string.Equals(detail.Row.Path, path, StringComparison.OrdinalIgnoreCase))
            detail.RefreshMuted();
    }

    /// <summary>Selects a task (from a notification), once the list has it.</summary>
    public void ShowTask(string path)
    {
        if (Editor is not null || Import is not null)
            return;   // a half-made change stays on screen
        _selectWhenSeen = path;
        SelectIfSeen();
    }

    /// <summary>A run has just failed and the person wants to hear about it. Raised on the UI thread.</summary>
    public event Action<FailureNotice>? RunFailed;

    void NotifyFailures(IReadOnlyList<TaskChange> changes)
    {
        if (RunFailed is not { } handler)
            return;
        foreach (var task in _failures.Check(changes, t => Settings.Wants(t) && !_organizer.IsMuted(t.Path)))
            handler(new FailureNotice(ComputerName, Host.IsRemote, task));
    }

    internal void SwitchTo(ISchedulerHost host)
    {
        ShowComputers = false;
        if (SwitchRequested is { } handler)
            handler(host);
        else
            host.Dispose();
    }

    public ObservableCollection<ViewItem> Views { get; }
    public ObservableCollection<FolderItem> Folders { get; } = [];
    public ObservableCollection<CollectionItem> Collections { get; } = [];
    [ObservableProperty] public partial CollectionItem? SelectedCollection { get; set; }

    // Making, renaming and deleting groups happens inline, in the sidebar and above the list.
    [ObservableProperty] public partial bool IsCreatingGroup { get; set; }
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(CreateGroupCommand))] public partial string NewGroupName { get; set; } = "";
    [ObservableProperty] public partial bool IsRenamingGroup { get; set; }
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SaveRenameCommand))] public partial string RenameText { get; set; } = "";
    [ObservableProperty] public partial bool IsConfirmingDelete { get; set; }

    /// <summary>What the list is showing, above its columns: "Starred", a group, a folder, "All tasks".</summary>
    public string ScopeTitle
    {
        get
        {
            var scope = SelectedCollection?.Title
                ?? (SelectedFolder is { Path: not "\\" } folder ? folder.Path : "All tasks");
            return SelectedView.Key == "all" ? scope : $"{scope} · {SelectedView.Title}";
        }
    }

    public bool ShowGroupTools => SelectedCollection?.IsGroup == true && !IsRenamingGroup && !IsConfirmingDelete;
    public string DeleteQuestion => $"Delete the group \"{SelectedCollection?.Title}\"? Its tasks stay exactly as they are.";
    public IReadOnlyList<string> GroupNames => _organizer.Groups.Select(g => g.Name).Order(StringComparer.CurrentCultureIgnoreCase).ToList();
    public ObservableCollection<TaskRowViewModel> Rows { get; } = [];
    public IReadOnlyList<SortOption> SortOptions { get; }

    [ObservableProperty] public partial ViewItem SelectedView { get; set; }
    [ObservableProperty] public partial FolderItem? SelectedFolder { get; set; }
    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty] public partial bool ShowWindowsTasks { get; set; }
    [ObservableProperty] public partial SortOption SelectedSort { get; set; }
    [ObservableProperty] public partial bool SortDescending { get; set; }
    [ObservableProperty] public partial TaskRowViewModel? SelectedRow { get; set; }
    [ObservableProperty] public partial TaskDetailViewModel? Detail { get; private set; }

    [ObservableProperty] public partial bool IsLoading { get; private set; } = true;
    [ObservableProperty] public partial string LiveText { get; private set; } = "Reading tasks…";
    [ObservableProperty] public partial string? LiveProblem { get; private set; }
    [ObservableProperty] public partial bool HistoryEnabled { get; private set; }
    [ObservableProperty] public partial bool HistoryBannerDismissed { get; set; }
    [ObservableProperty] public partial string? BannerProblem { get; private set; }
    [ObservableProperty] public partial string EmptyText { get; private set; } = "";
    [ObservableProperty] public partial int RunningCount { get; private set; }

    public bool ShowAdminBanner => !IsElevated;
    public bool ShowHistoryBanner => !HistoryEnabled && !HistoryBannerDismissed && !IsLoading;
    public bool HasLiveProblem => LiveProblem is not null;
    public bool HasBannerProblem => BannerProblem is not null;
    public bool HasDetail => Detail is not null;
    public bool IsEmpty => !IsLoading && Rows.Count == 0 && !ShowActivity;
    public string Title => $"Schedulry · {ComputerName}";
    public string HistoryBannerText => Host.IsRemote
        ? $"Task history is off on {ComputerName}. Turn it on to keep a record of every run there and see starts and finishes the instant they happen."
        : "Task history is off in Windows. Turn it on to keep a record of every run and see starts and finishes the instant they happen. Windows asks for administrator approval.";
    public string SortDirectionText => SortDescending ? "Descending" : "Ascending";

    partial void OnIsElevatedChanged(bool value) => OnPropertyChanged(nameof(ShowAdminBanner));
    partial void OnHistoryEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowHistoryBanner));
        OnPropertyChanged(nameof(ActivityNote));
        OnPropertyChanged(nameof(ActivityEmptyText));
        _ = Detail?.LoadRunsAsync();
        if (ShowActivity)
            _ = RefreshActivityAsync();
    }
    partial void OnHistoryBannerDismissedChanged(bool value) => OnPropertyChanged(nameof(ShowHistoryBanner));
    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowHistoryBanner));
        OnPropertyChanged(nameof(IsEmpty));
    }
    partial void OnLiveProblemChanged(string? value) => OnPropertyChanged(nameof(HasLiveProblem));
    partial void OnBannerProblemChanged(string? value) => OnPropertyChanged(nameof(HasBannerProblem));
    // The sidebar is one choice: a status, Starred or a group, or a folder. Choosing one clears the
    // others, so only one item is ever highlighted and only one filter applies.

    /// <summary>
    /// The status highlighted in the sidebar: none while Starred, a group or a folder is chosen
    /// (the list then shows every status in it).
    /// </summary>
    public ViewItem? HighlightedView
    {
        get => SelectedCollection is null && SelectedFolder is null ? SelectedView : null;
        set
        {
            // The status list writes null back when another list takes the highlight: nothing to do.
            if (value is null)
                return;
            if (value != SelectedView)
            {
                SelectedView = value;   // clears the other lists
                return;
            }
            ClearPlaces();
            RebuildRows();
            NotifyScope();
        }
    }

    partial void OnSelectedViewChanged(ViewItem value)
    {
        if (_quietNav)
            return;   // the list or folder being chosen rebuilds once
        ClearPlaces();
        RebuildRows();
        NotifyScope();
    }

    partial void OnSelectedFolderChanged(FolderItem? value)
    {
        if (_quietNav)
            return;
        if (value is not null)
            ClearOthers(keepFolder: true);
        RebuildRows();
        NotifyScope();
    }

    partial void OnSelectedCollectionChanged(CollectionItem? value)
    {
        IsRenamingGroup = false;
        IsConfirmingDelete = false;
        if (_quietNav)
            return;
        if (value is not null)
            ClearOthers(keepFolder: false);
        RebuildRows();
        NotifyScope();
    }

    /// <summary>Back to every status, and drops Starred, a group or a folder, without rebuilding.</summary>
    void ClearPlaces()
    {
        _quietNav = true;
        SelectedCollection = null;
        SelectedFolder = null;
        _quietNav = false;
    }

    /// <summary>A list or a folder was chosen: every status, and the other kind of place cleared.</summary>
    void ClearOthers(bool keepFolder)
    {
        _quietNav = true;
        SelectedView = Views[0];
        if (keepFolder)
            SelectedCollection = null;
        else
            SelectedFolder = null;
        _quietNav = false;
    }

    partial void OnIsRenamingGroupChanged(bool value) => OnPropertyChanged(nameof(ShowGroupTools));
    partial void OnIsConfirmingDeleteChanged(bool value) => OnPropertyChanged(nameof(ShowGroupTools));

    bool _quietNav;

    void NotifyScope()
    {
        OnPropertyChanged(nameof(HighlightedView));
        OnPropertyChanged(nameof(ScopeTitle));
        OnPropertyChanged(nameof(ShowGroupTools));
        OnPropertyChanged(nameof(DeleteQuestion));
        SelectionChanged();
    }
    partial void OnSearchTextChanged(string value)
    {
        RebuildRows();
        if (ShowActivity)
            _ = RefreshActivityAsync();
    }
    partial void OnSelectedSortChanged(SortOption value) => RebuildRows();
    partial void OnSortDescendingChanged(bool value)
    {
        OnPropertyChanged(nameof(SortDirectionText));
        RebuildRows();
    }
    partial void OnShowWindowsTasksChanged(bool value)
    {
        if (ShowActivity)
            _ = RefreshActivityAsync();
        RebuildFolders();
        RebuildCounts();
        RebuildRows();
    }

    partial void OnSelectedRowChanged(TaskRowViewModel? value)
    {
        Detail?.Dispose();
        Detail = value is null ? null : new TaskDetailViewModel(value, this);
        OnPropertyChanged(nameof(HasDetail));
        OnPropertyChanged(nameof(ShowSingleDetail));
    }

    /// <summary>Starts watching. The first read happens on the monitor's thread.</summary>
    public void Start() => Monitor.Start();

    [RelayCommand]
    void ToggleSortDirection() => SortDescending = !SortDescending;

    [RelayCommand]
    void ClearSearch() => SearchText = "";

    [RelayCommand]
    void RestartAsAdmin()
    {
        if (!Platform.RestartAsAdmin())
            BannerProblem = "The app is still running without administrator rights.";
    }

    [RelayCommand]
    async Task TurnOnHistoryAsync()
    {
        BannerProblem = null;
        bool done;
        if (Host.IsRemote)
        {
            // Another computer: no UAC prompt to ask through; the administrator connection is enough.
            string? problem = null;
            await Platform.Background(() =>
            {
                try
                {
                    Host.EnableHistory();
                }
                catch (Exception e)
                {
                    problem = e.Message;
                }
                return Task.CompletedTask;
            });
            done = problem is null;
            if (problem is not null)
            {
                BannerProblem = $"Task history is still off on {ComputerName}: {problem}";
                return;
            }
        }
        else
            done = await Platform.EnableHistory();
        if (!done)
        {
            BannerProblem = "Task history is still off.";
            return;
        }
        // The host caches its answer for a few seconds; ask it afresh so the next tick agrees and
        // the banner doesn't come back for a moment.
        var on = true;
        await Platform.Background(() =>
        {
            on = Host.RecheckHistory();
            return Task.CompletedTask;
        });
        HistoryEnabled = on;
        Monitor.Poke();
    }

    [RelayCommand]
    void DismissHistoryBanner() => HistoryBannerDismissed = true;

    // ---- Editor and import ----

    /// <summary>The editor page, while open (it takes the place of the list and preview).</summary>
    [ObservableProperty] public partial Editor.TaskEditorViewModel? Editor { get; private set; }
    [ObservableProperty] public partial Editor.ImportViewModel? Import { get; private set; }
    public bool IsEditing => Editor is not null;
    public bool IsBrowsing => Editor is null;
    public bool IsImporting => Import is not null;
    string? _selectWhenSeen;

    partial void OnEditorChanged(Editor.TaskEditorViewModel? value)
    {
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(IsBrowsing));
    }

    partial void OnImportChanged(Editor.ImportViewModel? value) => OnPropertyChanged(nameof(IsImporting));

    /// <summary>The folder a new task goes in by default: the one being looked at.</summary>
    string CurrentFolder => SelectedFolder is { Path: not "\\" } folder ? folder.Path : "\\";

    [RelayCommand]
    void NewTask() => Editor = new Editor.TaskEditorViewModel(this, null, folder: CurrentFolder);

    public void EditTask(TaskSnapshot task) => Editor = new Editor.TaskEditorViewModel(this, task);

    public void CopyTask(TaskSnapshot task) => Editor = new Editor.TaskEditorViewModel(this, task, copy: true);

    /// <summary>Back from the editor. A saved task is selected as soon as the monitor sees it.</summary>
    public void CloseEditor(string? savedPath)
    {
        Editor = null;
        AfterSave(savedPath);
    }

    [RelayCommand]
    async Task ImportTaskAsync()
    {
        if (await Platform.OpenTextFile("Import a Task", "Task Scheduler XML", "*.xml") is not { } file)
            return;
        Import = new Editor.ImportViewModel(this, file.Name, file.Text, CurrentFolder);
    }

    public void CloseImport(string? importedPath)
    {
        Import = null;
        AfterSave(importedPath);
    }

    void AfterSave(string? path)
    {
        if (path is null)
            return;
        _selectWhenSeen = path;
        Monitor.Poke();
        SelectIfSeen();
    }

    void SelectIfSeen()
    {
        if (_selectWhenSeen is not { } path || !_rows.TryGetValue(path, out var row))
            return;
        _selectWhenSeen = null;
        ShowActivity = false;
        if (!Rows.Contains(row))
        {
            // Somewhere the list isn't showing: show everything so it can be selected.
            SearchText = "";
            HighlightedView = Views[0];
            if (row.Task.IsWindowsTask)
                ShowWindowsTasks = true;
        }
        SelectedRow = row;
    }

    // ---- Recent activity ----

    /// <summary>Starts and finishes seen since the app opened, newest first, for when the history log is off.</summary>
    readonly List<RunLine> _seen = [];
    bool _activityStale = true;
    int _activityTicks;
    bool _activityLoading;

    [ObservableProperty] public partial bool ShowActivity { get; set; }
    public ObservableCollection<RunLine> Activity { get; } = [];
    [ObservableProperty] public partial RunLine? SelectedActivity { get; set; }

    public bool ShowTaskList => !ShowActivity;
    public string ActivityNote => HistoryEnabled
        ? "Every run in the last 24 hours, from Windows' task history. Select one to see its task."
        : "Task history is off in Windows, so this lists the starts and finishes Schedulry has seen since it opened.";
    public bool ActivityEmpty => !IsLoading && ShowActivity && Activity.Count == 0;
    public string ActivityEmptyText => HistoryEnabled
        ? "No runs in the last 24 hours."
        : "Nothing has started or finished since Schedulry opened. Runs appear here the moment they happen.";

    [RelayCommand]
    void ShowTasks() => ShowActivity = false;

    [RelayCommand]
    void ShowRecentActivity() => ShowActivity = true;

    partial void OnShowActivityChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowTaskList));
        OnPropertyChanged(nameof(ActivityEmpty));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(ActivityNote));
        if (value)
            _ = RefreshActivityAsync();
        else
            OnSelectedRowChanged(SelectedRow);   // back to the table's selection
    }

    partial void OnSelectedActivityChanged(RunLine? value)
    {
        if (value is null || !_rows.TryGetValue(value.Path, out var row))
            return;
        Detail?.Dispose();
        Detail = new TaskDetailViewModel(row, this);
        OnPropertyChanged(nameof(HasDetail));
        OnPropertyChanged(nameof(ShowSingleDetail));
    }

    bool ActivityVisible(string path)
    {
        var search = SearchText.Trim();
        if (search.Length > 0 && !path.Contains(search, StringComparison.OrdinalIgnoreCase))
            return false;
        return _rows.TryGetValue(path, out var row) ? InScope(row.Task) || IsCollected(path) : ShowWindowsTasks || !path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase);
    }

    bool IsCollected(string path) => _organizer.IsStarred(path) || _organizer.Groups.Any(g => g.Contains(path));

    /// <summary>Reloads Recent Activity: from the history log when it's on, else from what was seen.</summary>
    public async Task RefreshActivityAsync()
    {
        _activityStale = false;
        if (!HistoryEnabled)
        {
            ReplaceActivity(_seen.Where(l => ActivityVisible(l.Path)).ToList());
            return;
        }
        if (_activityLoading)
            return;
        _activityLoading = true;
        IReadOnlyList<RunRecord> runs = [];
        try
        {
            await Platform.Background(() =>
            {
                runs = Host.ReadRuns(null, _clock().AddDays(-1), 300);
                return Task.CompletedTask;
            });
        }
        catch (Exception e)
        {
            BannerProblem = $"Couldn't read the task history: {e.Message}";
        }
        finally
        {
            _activityLoading = false;
        }
        var now = _clock();
        ReplaceActivity(runs.Where(r => ActivityVisible(r.Path)).Select(r => RunLine.From(r, now)).ToList());
    }

    void ReplaceActivity(IReadOnlyList<RunLine> lines)
    {
        var selected = SelectedActivity;
        Activity.Clear();
        foreach (var line in lines)
            Activity.Add(line);
        if (selected is not null)
            SelectedActivity = Activity.FirstOrDefault(l => l.Path == selected.Path && l.Time == selected.Time);
        OnPropertyChanged(nameof(ActivityEmpty));
    }

    /// <summary>Called for every task update: notes a start or finish for Recent Activity.</summary>
    void NoteRun(TaskSnapshot before, TaskSnapshot after)
    {
        var started = after.State == RunState.Running && before.State != RunState.Running;
        var finished = (before.State == RunState.Running && after.State != RunState.Running)
            || (after.State != RunState.Running && after.LastRun != before.LastRun && after.LastRun is not null);
        if (!started && !finished)
            return;
        _activityStale = true;
        if (HistoryEnabled)
            return;   // the log has it; the next refresh shows it
        var line = RunLine.Seen(after, started, _clock());
        _seen.Insert(0, line);
        if (_seen.Count > 300)
            _seen.RemoveAt(_seen.Count - 1);
        if (ShowActivity && ActivityVisible(line.Path))
        {
            Activity.Insert(0, line);
            OnPropertyChanged(nameof(ActivityEmpty));
        }
    }

    // ---- Stars and groups ----

    [RelayCommand]
    public void ToggleStar(TaskRowViewModel? row)
    {
        if (row is null)
            return;
        row.IsStarred = !row.IsStarred;
        _organizer.SetStarred(row.Path, row.IsStarred);
        OrganizerChanged(row);
    }

    [RelayCommand]
    void StartNewGroup()
    {
        NewGroupName = "";
        IsCreatingGroup = true;
    }

    [RelayCommand]
    void CancelNewGroup()
    {
        IsCreatingGroup = false;
        _pendingForNewGroup = null;
    }

    bool CanCreateGroup() => NewGroupName.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanCreateGroup))]
    void CreateGroup()
    {
        var item = FindOrCreateGroup(NewGroupName);
        IsCreatingGroup = false;
        // Made from "New Group…" on selected tasks: they go in first.
        if (_pendingForNewGroup is { } pending)
        {
            _pendingForNewGroup = null;
            AddToCollection(pending, item);
        }
        SelectedCollection = item;
    }

    /// <summary>The group with this name (any case), made if there isn't one yet.</summary>
    public CollectionItem FindOrCreateGroup(string name)
    {
        name = name.Trim();
        var group = _organizer.Groups.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.CurrentCultureIgnoreCase));
        if (group is null)
        {
            group = new TaskGroup { Name = name };
            _organizer.Groups.Add(group);
            Save();
            RebuildCollections();
        }
        return Collections.First(c => c.Group == group);
    }

    /// <summary>Adds a task to a group by name (made if new). Used by the preview's "Add to Group".</summary>
    public void AddToGroup(TaskRowViewModel row, string groupName)
    {
        var item = FindOrCreateGroup(groupName);
        item.Group!.Add(row.Path);
        OrganizerChanged(row);
    }

    public void RemoveFromGroup(TaskRowViewModel row, TaskGroup group)
    {
        group.Remove(row.Path);
        OrganizerChanged(row);
    }

    /// <summary>The groups a task is in, by name.</summary>
    public IReadOnlyList<CollectionItem> GroupsOf(string path) =>
        Collections.Where(c => c.Group?.Contains(path) == true).ToList();

    [RelayCommand]
    void StartRename()
    {
        RenameText = SelectedCollection?.Title ?? "";
        IsRenamingGroup = true;
    }

    [RelayCommand]
    void CancelRename() => IsRenamingGroup = false;

    bool CanSaveRename() => RenameText.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanSaveRename))]
    void SaveRename()
    {
        if (SelectedCollection is not { Group: { } group } item)
            return;
        var name = RenameText.Trim();
        var other = _organizer.Groups.FirstOrDefault(g => g != group && string.Equals(g.Name, name, StringComparison.CurrentCultureIgnoreCase));
        if (other is not null)
        {
            // Same name as another group: the two become one.
            foreach (var path in group.Paths)
                other.Add(path);
            _organizer.Groups.Remove(group);
            Save();
            RebuildCollections();
            SelectedCollection = Collections.First(c => c.Group == other);
        }
        else
        {
            group.Name = name;
            item.Title = name;
            Save();
            RebuildCollections();
            SelectedCollection = Collections.First(c => c.Group == group);
        }
        IsRenamingGroup = false;
        NotifyScope();
        Detail?.RefreshGroups();
    }

    [RelayCommand]
    void AskDeleteGroup() => IsConfirmingDelete = true;

    [RelayCommand]
    void CancelDeleteGroup() => IsConfirmingDelete = false;

    [RelayCommand]
    void ConfirmDeleteGroup()
    {
        if (SelectedCollection?.Group is not { } group)
            return;
        _organizer.Groups.Remove(group);
        Save();
        IsConfirmingDelete = false;
        RebuildCollections();
        SelectedCollection = null;
        Detail?.RefreshGroups();
    }

    void OrganizerChanged(TaskRowViewModel row) => OrganizerChanged([row]);

    void OrganizerChanged(IReadOnlyList<TaskRowViewModel> rows)
    {
        Save();
        RebuildCollectionCounts();
        OnPropertyChanged(nameof(GroupNames));
        foreach (var row in rows)
        {
            // In Starred or a group, a task taken out leaves the list (unless it's selected: it stays until looked away from).
            var visible = Visible(row.Task);
            var listed = Rows.Contains(row);
            if (visible && !listed)
                InsertSorted(row);
            else if (!visible && listed && SelectedRow != row && !_selection.Contains(row))
                Rows.Remove(row);
            if (Detail?.Row == row)
                Detail.RefreshGroups();
        }
        SelectionChanged();
        UpdateEmptyText();
    }

    void Save()
    {
        try
        {
            _store.Save(_organizer);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            BannerProblem = $"Couldn't save your stars and groups: {e.Message}";
        }
    }

    void RebuildCollections()
    {
        var selected = SelectedCollection;
        var items = new List<CollectionItem> { new(null) };
        items.AddRange(_organizer.Groups.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).Select(g => new CollectionItem(g)));
        _quietNav = true;
        Collections.Clear();
        foreach (var item in items)
            Collections.Add(item);
        SelectedCollection = selected is null ? null
            : Collections.FirstOrDefault(c => c.Group == selected.Group);
        _quietNav = false;
        // The list on show went away (a deleted group): back to all tasks.
        if (selected is not null && SelectedCollection is null)
        {
            RebuildRows();
            NotifyScope();
        }
        RebuildCollectionCounts();
        OnPropertyChanged(nameof(GroupNames));
    }

    void RebuildCollectionCounts()
    {
        foreach (var item in Collections)
            item.Count = _rows.Keys.Count(path => InCollection(item, path));
    }

    void OnLoaded(IReadOnlyList<TaskSnapshot> tasks)
    {
        _failures.Seed(tasks);
        var now = _clock();
        _rows.Clear();
        foreach (var task in tasks)
            _rows[task.Path] = new TaskRowViewModel(task, now) { IsStarred = _organizer.IsStarred(task.Path) };
        IsLoading = false;
        RebuildFolders();
        RebuildCounts();
        RebuildRows();
        OnTicked();
    }

    void OnChanged(IReadOnlyList<TaskChange> changes)
    {
        NotifyFailures(changes);
        var now = _clock();
        var foldersChanged = false;
        foreach (var change in changes)
        {
            switch (change.Kind)
            {
                case ChangeKind.Removed:
                    if (_rows.Remove(change.Path, out var removed))
                    {
                        Rows.Remove(removed);
                        if (SelectedRow == removed)
                            SelectedRow = null;
                        foldersChanged = true;
                    }
                    break;
                case ChangeKind.Added when !_rows.ContainsKey(change.Path):
                    var row = new TaskRowViewModel(change.Task!, now) { IsStarred = _organizer.IsStarred(change.Path) };
                    _rows[change.Path] = row;
                    if (Visible(row.Task))
                        InsertSorted(row);
                    foldersChanged = true;
                    break;
                default:
                    var existing = _rows[change.Path];
                    var wasVisible = Rows.Contains(existing);
                    var before = existing.Task;
                    existing.Update(change.Task!, now);
                    NoteRun(before, existing.Task);
                    var visible = Visible(existing.Task);
                    // Rows stay where they are while visible, so the list doesn't jump under the pointer.
                    if (visible && !wasVisible)
                        InsertSorted(existing);
                    else if (!visible && wasVisible && SelectedRow != existing)
                        Rows.Remove(existing);
                    break;
            }
        }
        if (foldersChanged)
            RebuildFolders();
        RebuildCounts();
        UpdateEmptyText();
        SelectIfSeen();
    }

    /// <summary>Keeps "Running 0:42" and "In 3 min" moving between monitor ticks. The app calls it every second.</summary>
    public void RefreshClock()
    {
        var now = _clock();
        foreach (var row in _rows.Values)
            row.Refresh(now);
    }

    void OnTicked()
    {
        RefreshClock();
        HistoryEnabled = Host.HistoryEnabled;
        // Recent Activity from the log: soon after a run moves, and every 30 s regardless.
        if (ShowActivity && HistoryEnabled && (_activityStale || ++_activityTicks % 15 == 0))
            _ = RefreshActivityAsync();
        LiveProblem = null;
        RunningCount = _rows.Values.Count(r => r.IsRunning);
        LiveText = RunningCount switch
        {
            0 => "Live · nothing running",
            1 => "Live · 1 task running",
            var n => $"Live · {n} tasks running",
        };
    }

    bool InScope(TaskSnapshot task) => ShowWindowsTasks || !task.IsWindowsTask;

    bool InCollection(CollectionItem collection, string path) =>
        collection.Group is { } group ? group.Contains(path) : _organizer.IsStarred(path);

    bool Visible(TaskSnapshot task)
    {
        if (!SelectedView.Match(task))
            return false;
        // A starred or grouped Windows task is shown in its list even while Windows tasks are hidden:
        // putting it there was a choice to see it.
        if (SelectedCollection is { } collection)
        {
            if (!InCollection(collection, task.Path))
                return false;
        }
        else if (!InScope(task))
            return false;
        if (SelectedFolder is { Path: not "\\" } folder
            && !string.Equals(task.Folder, folder.Path, StringComparison.OrdinalIgnoreCase)
            && !task.Folder.StartsWith(folder.Path + "\\", StringComparison.OrdinalIgnoreCase))
            return false;
        var search = SearchText.Trim();
        if (search.Length == 0)
            return true;
        return task.Path.Contains(search, StringComparison.OrdinalIgnoreCase)
            || (task.Description?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
            || task.RunAs.Contains(search, StringComparison.OrdinalIgnoreCase)
            || task.Actions.Any(a => a.CommandLine.Contains(search, StringComparison.OrdinalIgnoreCase));
    }

    void RebuildRows()
    {
        var selected = SelectedRow;
        var rows = _rows.Values.Where(r => Visible(r.Task)).ToList();
        rows.Sort(Compare);
        Rows.Clear();
        foreach (var row in rows)
            Rows.Add(row);
        SelectedRow = selected is not null && rows.Contains(selected) ? selected : null;
        OnPropertyChanged(nameof(IsEmpty));
        UpdateEmptyText();
    }

    void InsertSorted(TaskRowViewModel row)
    {
        var index = 0;
        while (index < Rows.Count && Compare(Rows[index], row) <= 0)
            index++;
        Rows.Insert(index, row);
        OnPropertyChanged(nameof(IsEmpty));
    }

    int Compare(TaskRowViewModel a, TaskRowViewModel b)
    {
        int result = SelectedSort.Field switch
        {
            SortField.Status => StatusRank(a.Task).CompareTo(StatusRank(b.Task)),
            SortField.LastRun => Nullable.Compare(b.Task.LastRun, a.Task.LastRun),   // newest first when ascending
            SortField.NextRun => (a.Task.NextRun ?? DateTime.MaxValue).CompareTo(b.Task.NextRun ?? DateTime.MaxValue),
            SortField.Folder => string.Compare(a.Folder, b.Folder, StringComparison.CurrentCultureIgnoreCase),
            _ => 0,
        };
        if (result == 0)
            result = string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
        return SortDescending ? -result : result;
    }

    /// <summary>What most needs a look comes first: running, failed, warnings, then the rest.</summary>
    static int StatusRank(TaskSnapshot t) => t.State == RunState.Running ? 0
        : !t.Enabled ? 5
        : t.Outcome switch { TaskOutcome.Failed => 1, TaskOutcome.Warning => 2, TaskOutcome.NeverRun => 4, _ => 3 };

    void RebuildFolders()
    {
        var selectedPath = SelectedFolder?.Path;
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["\\"] = 0 };
        foreach (var task in _rows.Values.Select(r => r.Task).Where(InScope))
        {
            counts["\\"]++;
            // Each folder counts the tasks in it and below it, so a parent shows its whole branch.
            var folder = task.Folder;
            while (folder != "\\")
            {
                counts[folder] = counts.GetValueOrDefault(folder) + 1;
                var cut = folder.LastIndexOf('\\');
                folder = cut <= 0 ? "\\" : folder[..cut];
            }
        }
        var ordered = counts.Keys.OrderBy(p => p == "\\" ? "" : p, StringComparer.CurrentCultureIgnoreCase).ToList();
        Folders.Clear();
        foreach (var path in ordered)
            Folders.Add(new FolderItem(path, path == "\\" ? 0 : path.Count(c => c == '\\') - 1) { Count = counts[path] });
        // No folder chosen (a status or a list is): none to restore.
        _quietNav = true;
        if (selectedPath is null)
        {
            SelectedFolder = null;
            _quietNav = false;
            return;
        }
        // The same folder (by path) is re-selected quietly: the filter hasn't changed, so the list stays.
        // A folder that went away falls back to all tasks.
        SelectedFolder = Folders.FirstOrDefault(f => string.Equals(f.Path, selectedPath, StringComparison.OrdinalIgnoreCase));
        _quietNav = false;
        if (SelectedFolder is null)
        {
            RebuildRows();
            NotifyScope();
        }
    }

    void RebuildCounts()
    {
        var tasks = _rows.Values.Select(r => r.Task).Where(InScope).ToList();
        foreach (var view in Views)
            view.Count = tasks.Count(view.Match);
        RebuildCollectionCounts();
    }

    void UpdateEmptyText()
    {
        if (IsLoading || Rows.Count > 0)
        {
            EmptyText = "";
            return;
        }
        EmptyText = SearchText.Trim().Length > 0 ? $"No tasks match \"{SearchText.Trim()}\". Clear the search to see them all."
            : SelectedCollection is { IsStarredList: true } && SelectedView.Key == "all"
                ? "No starred tasks yet. Select the star on a task's row, or Star in its preview, to keep it here."
            : SelectedCollection is { IsGroup: true } && SelectedView.Key == "all"
                ? "This group is empty. Open a task and use Add to Group in its preview to put it here."
            : SelectedView.Key switch
            {
                "running" => "Nothing is running right now. Tasks appear here the moment they start.",
                "failed" => "No task failed on its last run.",
                "attention" => "Nothing needs a look.",
                "off" => "Every task is turned on.",
                "never" => "Every task has run at least once.",
                _ => "This folder has no tasks.",
            };
    }

    public void Dispose()
    {
        Settings.PropertyChanged -= OnSettingsChanged;
        Detail?.Dispose();
        Monitor.Dispose();
        Host.Dispose();
    }
}
