using Schedulry.Core;
using Schedulry.Demo;
using Schedulry.ViewModels;

namespace Schedulry.Tests;

public class HistoryTests
{
    // Background reads (history, next runs) finish before the next line, as in the snapshot tool.
    public HistoryTests() => Platform.Background = work => work();

    static readonly DateTime T = new(2026, 1, 7, 10, 0, 0);
    const string Path = @"\Backups\Copy";

    static HistoryEvent E(int id, int seconds, Guid run, int? code = null, string? detail = null) =>
        new(id, T.AddSeconds(seconds), run, Path, code, detail);

    [Fact]
    public void A_scheduled_run_that_succeeded()
    {
        var run = Guid.NewGuid();
        var built = Assert.Single(RunHistory.Build(
        [
            E(RunHistory.Finished, 42, run), E(RunHistory.ActionFinished, 41, run, 0),   // any order
            E(RunHistory.TriggeredBySchedule, 0, run), E(RunHistory.Started, 1, run),
            E(RunHistory.ProcessCreated, 1, run, detail: @"C:\Windows\System32\robocopy.exe"),
        ]));
        Assert.Equal(RunEnd.Succeeded, built.Outcome);
        Assert.Equal(RunCause.Schedule, built.Cause);
        Assert.Equal(T.AddSeconds(1), built.Start);
        Assert.Equal(TimeSpan.FromSeconds(41), built.Duration);
        Assert.Equal(@"C:\Windows\System32\robocopy.exe", built.Program);
        Assert.Equal("Succeeded", built.OutcomeText);
        Assert.Equal("On schedule", built.CauseText);
    }

    [Fact]
    public void Exit_codes_skips_stops_and_launch_failures()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid(), d = Guid.NewGuid(), e = Guid.NewGuid();
        var runs = RunHistory.Build(
        [
            E(RunHistory.TriggeredByPerson, 0, a), E(RunHistory.Started, 0, a), E(RunHistory.ActionFinished, 5, a, 1), E(RunHistory.Finished, 5, a),
            E(RunHistory.TriggeredBySchedule, 60, b), E(RunHistory.LaunchIgnoredStillRunning, 60, b),
            E(RunHistory.Started, 120, c), E(RunHistory.Terminated, 130, c),
            E(RunHistory.TriggeredBySchedule, 180, d), E(RunHistory.LaunchFailed, 180, d, unchecked((int)0x80070569)),
            E(RunHistory.Started, 240, e), E(RunHistory.ProcessCreated, 240, e),
        ]).ToDictionary(r => r.ActivityId);

        Assert.Equal(RunEnd.Failed, runs[a].Outcome);
        Assert.Equal("Exit code 1", runs[a].OutcomeText);
        Assert.Equal(RunCause.Person, runs[a].Cause);
        Assert.Equal(RunEnd.Skipped, runs[b].Outcome);
        Assert.Equal(RunEnd.Stopped, runs[c].Outcome);
        Assert.Equal(RunEnd.FailedToStart, runs[d].Outcome);
        Assert.Equal("Sign-in refused", runs[d].OutcomeText);
        Assert.Equal(RunEnd.Running, runs[e].Outcome);
        Assert.Null(runs[e].End);
    }

    [Fact]
    public void Runs_come_newest_first()
    {
        Guid older = Guid.NewGuid(), newer = Guid.NewGuid();
        var runs = RunHistory.Build([E(RunHistory.Started, 0, older), E(RunHistory.Finished, 3, older), E(RunHistory.Started, 100, newer), E(RunHistory.Finished, 103, newer)]);
        Assert.Equal([newer, older], runs.Select(r => r.ActivityId));
    }

    [Fact]
    public void Durations_read_briefly()
    {
        Assert.Equal("6 s", When.Took(TimeSpan.FromSeconds(6)));
        Assert.Equal("3 min 12 s", When.Took(new TimeSpan(0, 3, 12)));
        Assert.Equal("1 h 4 min", When.Took(new TimeSpan(1, 4, 30)));
        Assert.Equal("2 d 3 h", When.Took(new TimeSpan(2, 3, 0, 0)));
    }

    [Fact]
    public void Without_history_recent_activity_lists_what_was_seen()
    {
        var now = DateTime.Today.AddHours(10);
        using var vm = new MainViewModel(new DemoSchedulerHost(() => now), new MonitorTiming { StatusEveryTicks = 1, FullEveryTicks = 1000 }, () => now);
        vm.Monitor.Step();
        vm.ShowActivity = true;
        Assert.True(vm.ActivityEmpty);
        Assert.Contains("since it opened", vm.ActivityNote);

        now = now.AddSeconds(5);   // the watchdog's run finishes
        vm.Monitor.Step();
        var line = Assert.Single(vm.Activity);
        Assert.Equal("Printer queue watchdog", line.Name);
        Assert.StartsWith("Finished", line.WhatText);

        vm.SelectedActivity = line;   // shows that task in the preview
        Assert.Equal(line.Path, vm.Detail!.Row.Path);
    }

    [Fact]
    public void With_history_the_preview_and_activity_read_the_log()
    {
        var now = DateTime.Today.AddHours(10);
        using var vm = new MainViewModel(new DemoSchedulerHost(() => now, historyEnabled: true), clock: () => now);
        vm.Monitor.Step();
        vm.SelectedRow = vm.Rows.Single(r => r.Name == "Sync payroll exports");
        Assert.False(vm.Detail!.HistoryOff);
        Assert.Contains(vm.Detail.Runs, r => r.WhatText == "Sign-in refused");
        Assert.Contains(vm.Detail.Runs, r => r.WhatText == "Succeeded");

        vm.ShowActivity = true;
        Assert.NotEmpty(vm.Activity);
        Assert.Equal(vm.Activity.OrderByDescending(l => l.Time).Select(l => l.Time), vm.Activity.Select(l => l.Time));
        Assert.DoesNotContain(vm.Activity, l => l.Path.StartsWith(@"\Microsoft\"));   // Windows tasks hidden
    }

    [Fact]
    public void Delete_asks_first_then_removes_the_task()
    {
        var now = DateTime.Today.AddHours(10);
        using var vm = new MainViewModel(new DemoSchedulerHost(() => now), new MonitorTiming { StatusEveryTicks = 1, FullEveryTicks = 1000 }, () => now);
        vm.Monitor.Step();
        vm.SelectedRow = vm.Rows.Single(r => r.Name == "Clear temp folders");
        var detail = vm.Detail!;
        detail.AskDeleteCommand.Execute(null);
        Assert.True(detail.IsConfirmingDelete);
        Assert.Contains(vm.Rows, r => r.Name == "Clear temp folders");   // nothing gone yet

        detail.ConfirmDeleteCommand.Execute(null);
        vm.Monitor.Step();
        Assert.DoesNotContain(vm.Rows, r => r.Name == "Clear temp folders");
        Assert.Null(vm.SelectedRow);
    }

    [Fact]
    public void Delete_is_disabled_for_other_accounts_tasks_without_admin()
    {
        var now = DateTime.Today.AddHours(10);
        using var vm = new MainViewModel(new DemoSchedulerHost(() => now), clock: () => now);
        vm.Monitor.Step();
        vm.SelectedRow = vm.Rows.Single(r => r.Name == "Mirror data to NAS");
        Assert.False(vm.Detail!.AskDeleteCommand.CanExecute(null));
    }
}
