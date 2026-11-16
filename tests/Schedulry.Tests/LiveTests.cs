using System.Text.RegularExpressions;
using Schedulry.Core;
using Schedulry.Demo;
using Schedulry.ViewModels;

namespace Schedulry.Tests;

/// <summary>The monitor and the main view against the demo computer, with a clock the test moves.</summary>
public class LiveTests
{
    DateTime _now = DateTime.Today.AddHours(10);   // watchdog mid-run (see the snapshot tool)

    DemoSchedulerHost Host(bool elevated = false) => new(() => _now, elevated);

    static MonitorTiming EveryTick => new() { StatusEveryTicks = 1, FullEveryTicks = 1000 };

    [Fact]
    public void First_step_loads_everything_once()
    {
        using var monitor = new LiveMonitor(Host(), EveryTick);
        IReadOnlyList<TaskSnapshot>? loaded = null;
        var loads = 0;
        monitor.Loaded += tasks => { loaded = tasks; loads++; };
        monitor.Step();
        monitor.Step();
        Assert.Equal(1, loads);
        Assert.Contains(loaded!, t => t.Path == @"\Printer queue watchdog" && t.State == RunState.Running);
    }

    [Fact]
    public void A_finish_and_a_start_are_reported_without_a_full_read()
    {
        using var monitor = new LiveMonitor(Host(), EveryTick);
        monitor.Step();
        var changes = new List<TaskChange>();
        monitor.Changed += batch => changes.AddRange(batch);

        // The watchdog's run started at 9:59:57 and lasts 6 s; the next one starts at 10:00:57.
        _now = _now.AddMilliseconds(500);   // still running: nothing to report for the watchdog
        monitor.Step();
        Assert.DoesNotContain(changes, c => c.Path == @"\Printer queue watchdog");

        _now = _now.AddSeconds(3);   // 10:00:03.5, past its run
        monitor.Step();
        var finished = Assert.Single(changes, c => c.Path == @"\Printer queue watchdog");
        Assert.Equal(RunState.Ready, finished.Task!.State);
        Assert.Equal(0, finished.Task.LastResult);
        Assert.Null(finished.Task.RunningSince);

        changes.Clear();
        _now = _now.AddSeconds(54);  // 10:00:57.5: the next loop has started
        monitor.Step();
        Assert.Contains(changes, c => c.Path == @"\Printer queue watchdog" && c.Task!.State == RunState.Running);
    }

    [Fact]
    public void Run_from_the_app_shows_running_on_the_next_step()
    {
        var host = Host(elevated: true);
        using var monitor = new LiveMonitor(host, EveryTick);
        monitor.Step();
        var changes = new List<TaskChange>();
        monitor.Changed += batch => changes.AddRange(batch);

        host.Run(@"\Clear temp folders");
        monitor.Poke(@"\Clear temp folders");
        monitor.Step();
        Assert.Contains(changes, c => c.Path == @"\Clear temp folders" && c.Task!.State == RunState.Running);
    }

    [Fact]
    public void Main_view_hides_windows_tasks_until_asked_and_counts_by_status()
    {
        using var vm = new MainViewModel(Host(), EveryTick, () => _now);
        vm.Monitor.Step();
        Assert.DoesNotContain(vm.Rows, r => r.Task.IsWindowsTask);
        var all = vm.Rows.Count;

        vm.ShowWindowsTasks = true;
        Assert.Contains(vm.Rows, r => r.Task.IsWindowsTask);
        Assert.True(vm.Rows.Count > all);

        vm.SelectedView = vm.Views.Single(v => v.Key == "running");
        Assert.All(vm.Rows, r => Assert.Equal(RunState.Running, r.Task.State));
        Assert.Equal(vm.Rows.Count, vm.Views.Single(v => v.Key == "running").Count);
    }

    [Fact]
    public void Search_matches_programs_and_accounts()
    {
        using var vm = new MainViewModel(Host(), EveryTick, () => _now);
        vm.Monitor.Step();
        vm.SearchText = "robocopy";
        Assert.Equal("Mirror data to NAS", Assert.Single(vm.Rows).Name);
        vm.SearchText = "svc-reports";
        Assert.All(vm.Rows, r => Assert.Equal(@"\Reports", r.Folder));
        vm.SearchText = "zzz-nothing";
        Assert.True(vm.IsEmpty);
        Assert.Contains("Clear the search", vm.EmptyText);
    }

    [Fact]
    public void A_live_change_updates_the_row_in_place_and_glows()
    {
        using var vm = new MainViewModel(Host(), EveryTick, () => _now);
        vm.Monitor.Step();
        var row = vm.Rows.Single(r => r.Name == "Printer queue watchdog");
        var index = vm.Rows.IndexOf(row);
        Assert.True(row.IsRunning);

        _now = _now.AddSeconds(5);
        vm.Monitor.Step();
        Assert.Same(row, vm.Rows[index]);
        Assert.False(row.IsRunning);
        Assert.True(row.IsFresh);
        Assert.Equal("Ready", row.StatusText);

        _now = _now.AddSeconds(4);   // the glow fades
        vm.Monitor.Step();
        Assert.False(row.IsFresh);
    }

    [Fact]
    public void Buttons_that_cannot_act_are_disabled_with_a_reason()
    {
        using var vm = new MainViewModel(Host(elevated: false), EveryTick, () => _now);
        vm.Monitor.Step();
        vm.SelectedRow = vm.Rows.Single(r => r.Name == "Mirror data to NAS");   // runs as a service account
        Assert.False(vm.Detail!.RunCommand.CanExecute(null));
        Assert.NotNull(vm.Detail.ActionNote);

        vm.SelectedRow = vm.Rows.Single(r => r.Name == "Clear temp folders");     // runs as the current user
        Assert.True(vm.Detail!.RunCommand.CanExecute(null));
        Assert.False(vm.Detail.EndCommand.CanExecute(null));
        Assert.Null(vm.Detail.ActionNote);
    }

    [Fact]
    public void Light_and_dark_themes_define_the_same_tokens()
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Tokens.axaml"));
        var light = Keys(text, "Light");
        var dark = Keys(text, "Dark");
        Assert.NotEmpty(light);
        Assert.Equal(light.Order(), dark.Order());

        static List<string> Keys(string xaml, string theme)
        {
            var start = xaml.IndexOf($"x:Key=\"{theme}\"", StringComparison.Ordinal);
            var end = xaml.IndexOf("</ResourceDictionary>", start, StringComparison.Ordinal);
            return Regex.Matches(xaml[start..end], "x:Key=\"([^\"]+)\"").Select(m => m.Groups[1].Value).Skip(1).ToList();
        }
    }
}
