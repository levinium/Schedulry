using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Schedulry.ViewModels;

/// <summary>
/// Several tasks selected at once (Shift and Ctrl in the table), and putting them into a group:
/// from the preview, from the right-click menu, or by dragging them onto a group in the sidebar.
/// </summary>
public sealed partial class MainViewModel
{
    readonly List<TaskRowViewModel> _selection = [];
    List<TaskRowViewModel>? _pendingForNewGroup;

    /// <summary>The table's selected rows, in the order they were picked.</summary>
    public IReadOnlyList<TaskRowViewModel> SelectedRows => _selection;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMultiple), nameof(SelectionText), nameof(ShowSingleDetail), nameof(SelectionStarText))]
    public partial int SelectionCount { get; private set; }

    /// <summary>More than one task selected: the preview shows what can be done to all of them.</summary>
    public bool HasMultiple => SelectionCount > 1;

    /// <summary>One task's preview, when exactly one is selected.</summary>
    public bool ShowSingleDetail => HasDetail && !HasMultiple;

    public string SelectionText => $"{SelectionCount} tasks selected";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddSelectionToGroupCommand))]
    public partial string SelectionGroupText { get; set; } = "";

    /// <summary>All selected tasks are starred: the button takes the stars off instead.</summary>
    public bool AllSelectedStarred => _selection.Count > 0 && _selection.All(r => r.IsStarred);
    public string SelectionStarText => AllSelectedStarred ? "Unstar All" : "Star All";

    /// <summary>Viewing a group: the selected tasks can be taken out of it.</summary>
    public bool CanRemoveSelectionFromGroup => SelectedCollection?.Group is { } group && _selection.Any(r => group.Contains(r.Path));
    public string RemoveSelectionText => SelectedCollection?.Group is { } group ? $"Remove from {group.Name}" : "";

    /// <summary>The table's selection changed. The first row stays <see cref="SelectedRow"/>.</summary>
    public void SetSelection(IEnumerable<TaskRowViewModel> rows)
    {
        _selection.Clear();
        _selection.AddRange(rows.Distinct());
        SelectionCount = _selection.Count;
        SelectionChanged();
    }

    void SelectionChanged()
    {
        OnPropertyChanged(nameof(AllSelectedStarred));
        OnPropertyChanged(nameof(SelectionStarText));
        OnPropertyChanged(nameof(CanRemoveSelectionFromGroup));
        OnPropertyChanged(nameof(RemoveSelectionText));
    }

    /// <summary>Puts tasks into a group by name, made if new. One save for all of them.</summary>
    public void AddToGroup(IReadOnlyList<TaskRowViewModel> rows, string groupName)
    {
        if (rows.Count == 0 || groupName.Trim().Length == 0)
            return;
        AddToCollection(rows, FindOrCreateGroup(groupName));
    }

    /// <summary>
    /// Puts tasks into a sidebar list: a group, or Starred (which stars them). What a drop on the
    /// sidebar does.
    /// </summary>
    public void AddToCollection(IReadOnlyList<TaskRowViewModel> rows, CollectionItem target)
    {
        if (rows.Count == 0)
            return;
        if (target.Group is { } group)
        {
            foreach (var row in rows)
                group.Add(row.Path);
        }
        else
        {
            foreach (var row in rows)
            {
                row.IsStarred = true;
                _organizer.SetStarred(row.Path, true);
            }
        }
        OrganizerChanged(rows);
    }

    bool CanAddSelectionToGroup() => _selection.Count > 0 && SelectionGroupText.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanAddSelectionToGroup))]
    void AddSelectionToGroup()
    {
        AddToGroup(_selection.ToList(), SelectionGroupText);
        SelectionGroupText = "";
    }

    [RelayCommand]
    void ToggleSelectionStar()
    {
        var star = !AllSelectedStarred;
        foreach (var row in _selection)
        {
            row.IsStarred = star;
            _organizer.SetStarred(row.Path, star);
        }
        OrganizerChanged(_selection.ToList());
    }

    [RelayCommand]
    void RemoveSelectionFromGroup()
    {
        if (SelectedCollection?.Group is not { } group)
            return;
        RemoveFromGroup(_selection.ToList(), group);
    }

    public void RemoveFromGroup(IReadOnlyList<TaskRowViewModel> rows, Schedulry.Core.TaskGroup group)
    {
        foreach (var row in rows)
            group.Remove(row.Path);
        OrganizerChanged(rows);
    }

    /// <summary>"New Group…" for the selected tasks: the sidebar's New Group box opens, and Create puts them in.</summary>
    public void StartNewGroupFor(IReadOnlyList<TaskRowViewModel> rows)
    {
        _pendingForNewGroup = rows.ToList();
        StartNewGroup();
    }
}
