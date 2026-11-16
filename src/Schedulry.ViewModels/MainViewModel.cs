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

    public MainViewModel(ISchedulerHost host, MonitorTiming? timing = null, Func<DateTime>? clock = null, IOrganizerStore? organizer = null)
    {
        Host = host;
        _clock = clock ?? (() => DateTime.Now);
        _store = organizer ?? new MemoryOrganizerStore();
        _organizer = _store.Load();
        RebuildCollections();
        Monitor = new LiveMonitor(host, timing);
        IsElevated = host.IsElevated;
        CurrentUser = host.CurrentUser;
        ComputerName = host.ComputerName;

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
    }

    public ISchedulerHost Host { get; }
    public LiveMonitor Monitor { get; }
    public string ComputerName { get; }
    public string CurrentUser { get; }
    [ObservableProperty] public partial bool IsElevated { get; private set; }

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
    public bool IsEmpty => !IsLoading && Rows.Count == 0;
    public string Title => $"Schedulry · {ComputerName}";
    public string SortDirectionText => SortDescending ? "Descending" : "Ascending";

    partial void OnIsElevatedChanged(bool value) => OnPropertyChanged(nameof(ShowAdminBanner));
    partial void OnHistoryEnabledChanged(bool value) => OnPropertyChanged(nameof(ShowHistoryBanner));
    partial void OnHistoryBannerDismissedChanged(bool value) => OnPropertyChanged(nameof(ShowHistoryBanner));
    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowHistoryBanner));
        OnPropertyChanged(nameof(IsEmpty));
    }
    partial void OnLiveProblemChanged(string? value) => OnPropertyChanged(nameof(HasLiveProblem));
    partial void OnBannerProblemChanged(string? value) => OnPropertyChanged(nameof(HasBannerProblem));
    partial void OnSelectedViewChanged(ViewItem value)
    {
        RebuildRows();
        OnPropertyChanged(nameof(ScopeTitle));
    }

    // A folder and a My Tasks list are two kinds of place: choosing one clears the other.
    partial void OnSelectedFolderChanged(FolderItem? value)
    {
        if (_quietFolder)
            return;
        if (value is not null && SelectedCollection is not null)
        {
            _quietCollection = true;
            SelectedCollection = null;
            _quietCollection = false;
        }
        RebuildRows();
        NotifyScope();
    }

    partial void OnSelectedCollectionChanged(CollectionItem? value)
    {
        IsRenamingGroup = false;
        IsConfirmingDelete = false;
        if (_quietCollection)
            return;
        _quietFolder = true;
        SelectedFolder = value is null ? Folders.FirstOrDefault() : null;
        _quietFolder = false;
        RebuildRows();
        NotifyScope();
    }

    partial void OnIsRenamingGroupChanged(bool value) => OnPropertyChanged(nameof(ShowGroupTools));
    partial void OnIsConfirmingDeleteChanged(bool value) => OnPropertyChanged(nameof(ShowGroupTools));

    bool _quietCollection;

    void NotifyScope()
    {
        OnPropertyChanged(nameof(ScopeTitle));
        OnPropertyChanged(nameof(ShowGroupTools));
        OnPropertyChanged(nameof(DeleteQuestion));
    }
    partial void OnSearchTextChanged(string value) => RebuildRows();
    partial void OnSelectedSortChanged(SortOption value) => RebuildRows();
    partial void OnSortDescendingChanged(bool value)
    {
        OnPropertyChanged(nameof(SortDirectionText));
        RebuildRows();
    }
    partial void OnShowWindowsTasksChanged(bool value)
    {
        RebuildFolders();
        RebuildCounts();
        RebuildRows();
    }

    partial void OnSelectedRowChanged(TaskRowViewModel? value)
    {
        Detail?.Dispose();
        Detail = value is null ? null : new TaskDetailViewModel(value, this);
        OnPropertyChanged(nameof(HasDetail));
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
        var done = await Platform.EnableHistory();
        if (!done)
        {
            BannerProblem = "Task history is still off.";
            return;
        }
        HistoryEnabled = true;
        Monitor.Poke();
    }

    [RelayCommand]
    void DismissHistoryBanner() => HistoryBannerDismissed = true;

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
    void CancelNewGroup() => IsCreatingGroup = false;

    bool CanCreateGroup() => NewGroupName.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanCreateGroup))]
    void CreateGroup()
    {
        var item = FindOrCreateGroup(NewGroupName);
        IsCreatingGroup = false;
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

    void OrganizerChanged(TaskRowViewModel row)
    {
        Save();
        RebuildCollectionCounts();
        OnPropertyChanged(nameof(GroupNames));
        // In Starred or a group, a task taken out leaves the list (unless it's the one being looked at).
        var visible = Visible(row.Task);
        var listed = Rows.Contains(row);
        if (visible && !listed)
            InsertSorted(row);
        else if (!visible && listed && SelectedRow != row)
            Rows.Remove(row);
        if (Detail?.Row == row)
            Detail.RefreshGroups();
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
        _quietCollection = true;
        Collections.Clear();
        foreach (var item in items)
            Collections.Add(item);
        SelectedCollection = selected is null ? null
            : Collections.FirstOrDefault(c => c.Group == selected.Group);
        _quietCollection = false;
        // The list on show went away (a deleted group): back to all tasks.
        if (selected is not null && SelectedCollection is null)
        {
            _quietFolder = true;
            SelectedFolder = Folders.FirstOrDefault();
            _quietFolder = false;
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
                    row.Update(change.Task!, now);   // new rows glow too
                    _rows[change.Path] = row;
                    if (Visible(row.Task))
                        InsertSorted(row);
                    foldersChanged = true;
                    break;
                default:
                    var existing = _rows[change.Path];
                    var wasVisible = Rows.Contains(existing);
                    existing.Update(change.Task!, now);
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
        var selectedPath = SelectedFolder?.Path ?? "\\";
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
        // While Starred or a group is showing, no folder is selected.
        if (SelectedCollection is not null)
        {
            _quietFolder = true;
            SelectedFolder = null;
            _quietFolder = false;
            return;
        }
        // The same folder (by path) is re-selected quietly: the filter hasn't changed, so the list stays.
        var restored = Folders.FirstOrDefault(f => string.Equals(f.Path, selectedPath, StringComparison.OrdinalIgnoreCase)) ?? Folders[0];
        var samePath = string.Equals(restored.Path, selectedPath, StringComparison.OrdinalIgnoreCase);
        _quietFolder = samePath;
        SelectedFolder = restored;
        _quietFolder = false;
    }

    bool _quietFolder;

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
        Detail?.Dispose();
        Monitor.Dispose();
        Host.Dispose();
    }
}
