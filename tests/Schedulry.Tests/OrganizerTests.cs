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
}
