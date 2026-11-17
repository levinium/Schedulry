using Schedulry.Core;
using Schedulry.Demo;
using Schedulry.ViewModels;

namespace Schedulry.Tests;

public class OrganizerTests
{
    readonly DateTime _now = DateTime.Today.AddHours(10);

    MainViewModel Open(IOrganizerStore store)
    {
        var vm = new MainViewModel(new DemoSchedulerHost(() => _now), clock: () => _now, organizer: store);
        vm.Monitor.Step();
        return vm;
    }

    [Fact]
    public void Starring_a_task_puts_it_in_starred_and_saves()
    {
        var store = new MemoryOrganizerStore();
        using var vm = Open(store);
        var row = vm.Rows.Single(r => r.Name == "Clear temp folders");
        vm.ToggleStar(row);
        Assert.True(store.Load().IsStarred(@"\Clear temp folders"));

        vm.SelectedCollection = vm.Collections.Single(c => c.IsStarredList);
        Assert.Same(row, Assert.Single(vm.Rows));
        Assert.Equal(1, vm.SelectedCollection.Count);
        Assert.Null(vm.SelectedFolder);   // a place is a folder or a list, not both
        Assert.Equal("Starred", vm.ScopeTitle);

        vm.SelectedRow = null;
        vm.ToggleStar(row);
        Assert.Empty(vm.Rows);
        Assert.Contains("No starred tasks", vm.EmptyText);
    }

    [Fact]
    public void The_sidebar_highlights_one_item_and_applies_one_filter()
    {
        using var vm = Open(new MemoryOrganizerStore(DemoOrganizer.Sample()));
        var all = vm.Rows.Count;
        Assert.Same(vm.Views[0], vm.HighlightedView);
        Assert.Null(vm.SelectedFolder);

        // A status, then a group: the status goes, and the group shows every status.
        var off = vm.Views.Single(v => v.Key == "off");
        vm.HighlightedView = off;
        Assert.Same(off, vm.SelectedView);
        var group = vm.Collections.First(c => c.IsGroup);
        vm.SelectedCollection = group;
        Assert.Null(vm.HighlightedView);
        Assert.Equal("all", vm.SelectedView.Key);
        Assert.Equal(group.Count, vm.Rows.Count);
        Assert.Equal(group.Title, vm.ScopeTitle);

        // A status again: the group goes.
        vm.HighlightedView = off;
        Assert.Null(vm.SelectedCollection);
        Assert.Same(off, vm.HighlightedView);
        Assert.All(vm.Rows, r => Assert.False(r.Task.Enabled));

        // A folder: the status goes; then the list again by "All".
        var folder = vm.Folders.First(f => f.Path != "\\");
        vm.SelectedFolder = folder;
        Assert.Null(vm.HighlightedView);
        Assert.Null(vm.SelectedCollection);
        Assert.Equal("all", vm.SelectedView.Key);
        vm.HighlightedView = vm.Views[0];
        Assert.Null(vm.SelectedFolder);
        Assert.Equal(all, vm.Rows.Count);
        Assert.Equal("All tasks", vm.ScopeTitle);

        // The list writing null back when another list takes the highlight changes nothing.
        vm.SelectedCollection = group;
        vm.HighlightedView = null;
        Assert.Same(group, vm.SelectedCollection);
    }

    [Fact]
    public void Add_to_group_by_name_makes_the_group_once()
    {
        using var vm = Open(new MemoryOrganizerStore());
        var row = vm.Rows.Single(r => r.Name == "Clear temp folders");
        vm.SelectedRow = row;
        vm.Detail!.GroupNameText = "Cleanup";
        vm.Detail.AddToGroupCommand.Execute(null);
        vm.SelectedRow = vm.Rows.Single(r => r.Name == "Rotate web logs");
        vm.Detail!.GroupNameText = "cleanup";   // same group, any case
        vm.Detail.AddToGroupCommand.Execute(null);

        var group = Assert.Single(vm.Collections, c => c.IsGroup);
        Assert.Equal("Cleanup", group.Title);
        Assert.Equal(2, group.Count);
        Assert.Equal("Cleanup", Assert.Single(vm.Detail.Groups).Title);

        vm.Detail.RemoveFromGroupCommand.Execute(group);
        Assert.Empty(vm.Detail.Groups);
        Assert.Equal(1, group.Count);
    }

    [Fact]
    public void Starred_windows_tasks_show_in_their_list_while_windows_tasks_are_hidden()
    {
        using var vm = Open(new MemoryOrganizerStore(DemoOrganizer.Sample()));
        Assert.False(vm.ShowWindowsTasks);
        vm.SelectedCollection = vm.Collections.Single(c => c.Title == "Overnight jobs");
        Assert.Contains(vm.Rows, r => r.Name == "ScheduledDefrag");
        Assert.Equal(4, vm.Rows.Count);
    }

    [Fact]
    public void Rename_into_an_existing_name_merges_and_delete_keeps_tasks()
    {
        var store = new MemoryOrganizerStore(DemoOrganizer.Sample());
        using var vm = Open(store);
        var all = vm.Views.Single(v => v.Key == "all").Count;
        vm.SelectedCollection = vm.Collections.Single(c => c.Title == "Finance");
        vm.StartRenameCommand.Execute(null);
        vm.RenameText = "overnight JOBS";
        vm.SaveRenameCommand.Execute(null);
        var merged = Assert.Single(vm.Collections, c => c.IsGroup);
        Assert.Equal("Overnight jobs", merged.Title);
        Assert.Equal(7, merged.Count);

        vm.AskDeleteGroupCommand.Execute(null);
        Assert.True(vm.IsConfirmingDelete);
        vm.ConfirmDeleteGroupCommand.Execute(null);
        Assert.DoesNotContain(vm.Collections, c => c.IsGroup);
        Assert.Empty(store.Load().Groups);
        Assert.Equal(all, vm.Rows.Count);   // back on all tasks, none lost
    }

    [Fact]
    public void Json_store_round_trips_and_survives_a_damaged_file()
    {
        var folder = Path.Combine(Path.GetTempPath(), "schedulry-tests-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(folder, "organize.json");
        try
        {
            var store = new JsonOrganizerStore(file);
            store.Save(DemoOrganizer.Sample());
            var back = new JsonOrganizerStore(file).Load();
            Assert.True(back.IsStarred(@"\MIRROR DATA TO NAS"));
            Assert.Equal(2, back.Groups.Count);

            File.WriteAllText(file, "{ not json");
            Assert.Empty(new JsonOrganizerStore(file).Load().Groups);
            Assert.True(File.Exists(file + ".damaged"));
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    // ---- Several tasks at once ----

    (MainViewModel Vm, List<TaskRowViewModel> Rows) OpenWithTwoSelected(IOrganizerStore? store = null)
    {
        var vm = Open(store ?? new MemoryOrganizerStore());
        var rows = vm.Rows.Where(r => r.Name is "Clear temp folders" or "Rotate web logs").ToList();
        vm.SelectedRow = rows[0];
        vm.SetSelection(rows);
        return (vm, rows);
    }

    [Fact]
    public void Several_selected_tasks_go_into_a_group_together()
    {
        var store = new MemoryOrganizerStore();
        var (vm, rows) = OpenWithTwoSelected(store);
        using var _ = vm;
        Assert.True(vm.HasMultiple);
        Assert.False(vm.ShowSingleDetail);
        Assert.Equal("2 tasks selected", vm.SelectionText);
        Assert.False(vm.AddSelectionToGroupCommand.CanExecute(null));   // no name yet

        vm.SelectionGroupText = "Cleanup";
        vm.AddSelectionToGroupCommand.Execute(null);

        var group = Assert.Single(vm.Collections, c => c.IsGroup);
        Assert.Equal(2, group.Count);
        Assert.Equal("", vm.SelectionGroupText);
        Assert.All(rows, r => Assert.True(store.Load().Groups.Single().Contains(r.Path)));
    }

    [Fact]
    public void Dropping_tasks_on_starred_stars_them()
    {
        var (vm, rows) = OpenWithTwoSelected();
        using var _ = vm;
        Assert.Equal("Star All", vm.SelectionStarText);
        vm.AddToCollection(rows, vm.Collections.Single(c => c.IsStarredList));
        Assert.All(rows, r => Assert.True(r.IsStarred));
        Assert.Equal("Unstar All", vm.SelectionStarText);
        vm.ToggleSelectionStarCommand.Execute(null);
        Assert.All(rows, r => Assert.False(r.IsStarred));
    }

    [Fact]
    public void New_group_for_selected_tasks_puts_them_in_when_created()
    {
        var (vm, rows) = OpenWithTwoSelected();
        using var _ = vm;
        vm.StartNewGroupFor(rows);
        Assert.True(vm.IsCreatingGroup);
        vm.NewGroupName = "Nightly";
        vm.CreateGroupCommand.Execute(null);
        var group = Assert.Single(vm.Collections, c => c.IsGroup);
        Assert.Equal(2, group.Count);
        Assert.Same(group, vm.SelectedCollection);

        // Cancelled, a later group starts empty.
        vm.StartNewGroupFor(rows);
        vm.CancelNewGroupCommand.Execute(null);
        vm.StartNewGroupCommand.Execute(null);
        vm.NewGroupName = "Empty";
        vm.CreateGroupCommand.Execute(null);
        Assert.Equal(0, vm.Collections.Single(c => c.Title == "Empty").Count);
    }

    [Fact]
    public void Selected_tasks_taken_out_of_a_group_stay_until_looked_away_from()
    {
        var (vm, rows) = OpenWithTwoSelected();
        using var _ = vm;
        vm.AddToGroup(rows, "Cleanup");
        vm.SelectedCollection = vm.Collections.Single(c => c.IsGroup);
        vm.SelectedRow = rows[0];
        vm.SetSelection(rows);
        Assert.True(vm.CanRemoveSelectionFromGroup);
        Assert.Equal("Remove from Cleanup", vm.RemoveSelectionText);

        vm.RemoveSelectionFromGroupCommand.Execute(null);
        Assert.Equal(0, vm.SelectedCollection!.Count);
        Assert.Equal(2, vm.Rows.Count);   // still showing while selected
        Assert.False(vm.CanRemoveSelectionFromGroup);
    }
}
