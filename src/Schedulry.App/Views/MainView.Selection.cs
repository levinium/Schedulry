using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Schedulry.ViewModels;

namespace Schedulry.App.Views;

/// <summary>
/// The table's selection of several tasks, its right-click menu, and dragging tasks from it onto
/// Starred or a group in the sidebar.
/// </summary>
public partial class MainView
{
    static readonly DataFormat<TaskRowViewModel[]> TasksFormat = DataFormat.CreateInProcessFormat<TaskRowViewModel[]>("schedulry-tasks");
    const double DragDistance = 6;

    PointerPressedEventArgs? _pressed;
    Point _pressedAt;
    TaskRowViewModel? _pressedRow;
    bool _keepSelectionOnPress;
    ListBoxItem? _dropTarget;

    MainViewModel? Model => DataContext as MainViewModel;

    void WireTaskSelection()
    {
        TaskGrid.SelectionChanged += (_, _) => Model?.SetSelection(TaskGrid.SelectedItems.OfType<TaskRowViewModel>());

        // Tunnel: before the table changes its selection, so pressing on one of several selected rows
        // can start dragging all of them.
        TaskGrid.AddHandler(PointerPressedEvent, OnGridPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        TaskGrid.AddHandler(PointerMovedEvent, OnGridMoved, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        TaskGrid.AddHandler(PointerReleasedEvent, OnGridReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);

        var menu = new ContextMenu();
        menu.Opening += (_, e) => e.Cancel = !FillTaskMenu(menu);
        TaskGrid.ContextMenu = menu;

        DragDrop.AddDragOverHandler(CollectionList, OnCollectionDragOver);
        DragDrop.AddDragLeaveHandler(CollectionList, (_, _) => SetDropTarget(null));
        DragDrop.AddDropHandler(CollectionList, OnCollectionDrop);
    }

    static TaskRowViewModel? RowAt(RoutedEventArgs e) =>
        (e.Source as Visual)?.FindAncestorOfType<DataGridRow>(includeSelf: true)?.DataContext as TaskRowViewModel;

    void OnGridPressed(object? sender, PointerPressedEventArgs e)
    {
        _pressed = null;
        _keepSelectionOnPress = false;
        var row = RowAt(e);
        if (row is null)
            return;
        var point = e.GetCurrentPoint(TaskGrid);
        var selected = TaskGrid.SelectedItems.Contains(row);
        if (point.Properties.IsRightButtonPressed)
        {
            // Right-click a row outside the selection: the menu is for that row.
            if (!selected)
                TaskGrid.SelectedItem = row;
            e.Handled = true;
            return;
        }
        if (!point.Properties.IsLeftButtonPressed)
            return;
        _pressed = e;
        _pressedAt = point.Position;
        _pressedRow = row;
        // A plain press on one of several selected rows keeps them all, ready to drag. A release
        // without dragging narrows the selection to that row, as a click normally would.
        if (selected && TaskGrid.SelectedItems.Count > 1 && e.KeyModifiers == KeyModifiers.None)
        {
            _keepSelectionOnPress = true;
            e.Handled = true;
        }
    }

    async void OnGridMoved(object? sender, PointerEventArgs e)
    {
        if (_pressed is not { } pressed || _pressedRow is null)
            return;
        var point = e.GetCurrentPoint(TaskGrid);
        if (!point.Properties.IsLeftButtonPressed)
        {
            _pressed = null;
            return;
        }
        var moved = point.Position - _pressedAt;
        if (Math.Abs(moved.X) < DragDistance && Math.Abs(moved.Y) < DragDistance)
            return;
        _pressed = null;
        _keepSelectionOnPress = false;
        var rows = TaskGrid.SelectedItems.OfType<TaskRowViewModel>().ToList();
        if (!rows.Contains(_pressedRow))
            rows = [_pressedRow];
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(TasksFormat, rows.ToArray()));
        try
        {
            await DragDrop.DoDragDropAsync(pressed, data, DragDropEffects.Copy);
        }
        finally
        {
            SetDropTarget(null);
        }
    }

    void OnGridReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_keepSelectionOnPress && _pressedRow is { } row)
            TaskGrid.SelectedItem = row;
        _keepSelectionOnPress = false;
        _pressed = null;
    }

    // ---- Dropping onto Starred or a group ----

    ListBoxItem? CollectionItemAt(DragEventArgs e) =>
        (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);

    void OnCollectionDragOver(object? sender, DragEventArgs e)
    {
        var item = e.DataTransfer.Contains(TasksFormat) ? CollectionItemAt(e) : null;
        e.DragEffects = item?.DataContext is CollectionItem ? DragDropEffects.Copy : DragDropEffects.None;
        SetDropTarget(e.DragEffects == DragDropEffects.None ? null : item);
        e.Handled = true;
    }

    void OnCollectionDrop(object? sender, DragEventArgs e)
    {
        SetDropTarget(null);
        if (CollectionItemAt(e)?.DataContext is not CollectionItem target || e.DataTransfer.TryGetValue(TasksFormat) is not { } rows)
            return;
        Model?.AddToCollection(rows, target);
        e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
    }

    void SetDropTarget(ListBoxItem? item)
    {
        if (_dropTarget == item)
            return;
        _dropTarget?.Classes.Remove("droptarget");
        _dropTarget = item;
        _dropTarget?.Classes.Add("droptarget");
    }

    // ---- Right-click menu ----

    /// <summary>Fills the menu for the selected tasks. False when nothing is selected (no menu).</summary>
    bool FillTaskMenu(ContextMenu menu)
    {
        if (Model is not { } model || model.SelectedRows.Count == 0)
            return false;
        var rows = model.SelectedRows.ToList();
        var items = new List<object>();

        var addTo = new MenuItem { Header = rows.Count == 1 ? "Add to Group" : $"Add {rows.Count} Tasks to Group" };
        var groups = new List<object>();
        foreach (var collection in model.Collections.Where(c => c.IsGroup))
        {
            var target = collection;
            var item = new MenuItem { Header = target.Title };
            item.Click += (_, _) => model.AddToCollection(rows, target);
            groups.Add(item);
        }
        if (groups.Count > 0)
            groups.Add(new Separator());
        var newGroup = new MenuItem { Header = "New Group…" };
        newGroup.Click += (_, _) => model.StartNewGroupFor(rows);
        groups.Add(newGroup);
        addTo.ItemsSource = groups;
        items.Add(addTo);

        if (model.CanRemoveSelectionFromGroup)
            items.Add(new MenuItem { Header = model.RemoveSelectionText, Command = model.RemoveSelectionFromGroupCommand });
        items.Add(new MenuItem { Header = rows.Count == 1 ? (rows[0].IsStarred ? "Unstar" : "Star") : model.SelectionStarText, Command = model.ToggleSelectionStarCommand });

        menu.ItemsSource = items;
        return true;
    }
}
