using Schedulry.Core;
using Schedulry.Demo;
using Schedulry.ViewModels;

namespace Schedulry.Tests;

/// <summary>Failure notifications, the settings behind them, and the monitor's quieter background pace.</summary>
public class NotificationTests
{
    DateTime _now = DateTime.Today.AddHours(10);

    static MonitorTiming EveryTick => new() { StatusEveryTicks = 1, FullEveryTicks = 1000 };

    static TaskSnapshot Task(string path, DateTime? lastRun, int? result, RunState state = RunState.Ready) =>
        new() { Path = path, LastRun = lastRun, LastResult = result, State = state };

    static TaskChange Updated(TaskSnapshot task) => new(ChangeKind.Updated, task.Path, task);

    [Fact]
    public void Only_a_new_failed_run_is_reported_and_only_once()
    {
        var nine = DateTime.Today.AddHours(9);
        var watch = new FailureWatch();
        watch.Seed([Task(@"\Backup", nine, 1), Task(@"\Report", nine, 0)]);

        // Already failed before watching began: not new.
        Assert.Empty(watch.Check([Updated(Task(@"\Backup", nine, 1) with { NextRun = nine.AddDays(1) })], _ => true));

        // A new run that fails is reported; seeing the same result again is not.
        var failed = Task(@"\Report", nine.AddHours(1), 2);
        Assert.Single(watch.Check([Updated(failed)], _ => true));
        Assert.Empty(watch.Check([Updated(failed)], _ => true));

        // Running, then a success: nothing.
        Assert.Empty(watch.Check([Updated(Task(@"\Report", nine.AddHours(2), 0x41301, RunState.Running))], _ => true));
        Assert.Empty(watch.Check([Updated(Task(@"\Report", nine.AddHours(2), 0))], _ => true));

        // A run that ends badly after starting (same start time, new result) is reported.
        Assert.Empty(watch.Check([Updated(Task(@"\Backup", nine.AddHours(3), 0x41301, RunState.Running))], _ => true));
        Assert.Single(watch.Check([Updated(Task(@"\Backup", nine.AddHours(3), 1))], _ => true));
    }

    [Fact]
    public void New_tasks_warnings_and_unwanted_tasks_are_not_reported()
    {
        var nine = DateTime.Today.AddHours(9);
        var watch = new FailureWatch();
        watch.Seed([Task(@"\Backup", nine, 0), Task(@"\Quiet", nine, 0)]);

        var imported = Task(@"\Imported", nine, 1);
        Assert.Empty(watch.Check([new TaskChange(ChangeKind.Added, imported.Path, imported)], _ => true));
        // Stopped by a person: a warning, not a failure.
        Assert.Empty(watch.Check([Updated(Task(@"\Backup", nine.AddHours(1), 0x41306))], _ => true));
        Assert.Empty(watch.Check([Updated(Task(@"\Quiet", nine.AddHours(1), 1))], t => t.Path != @"\Quiet"));
    }

    [Fact]
    public void A_removed_task_that_comes_back_brings_no_old_failure()
    {
        var nine = DateTime.Today.AddHours(9);
        var watch = new FailureWatch();
        watch.Seed([Task(@"\Backup", nine, 0)]);
        Assert.Empty(watch.Check([new TaskChange(ChangeKind.Removed, @"\Backup", null)], _ => true));
        var back = Task(@"\Backup", nine.AddHours(1), 1);
        Assert.Empty(watch.Check([new TaskChange(ChangeKind.Added, back.Path, back)], _ => true));
    }

    [Fact]
    public void Notice_reads_plainly_and_names_another_computer()
    {
        var task = Task(@"\Reports\Weekly disk space summary", DateTime.Today.AddHours(10).AddMinutes(2), unchecked((int)0x80070002));
        var here = new FailureNotice("WS01", false, task);
        Assert.Equal("Weekly disk space summary failed", here.Title);
        Assert.StartsWith("Program not found · ", here.Body);
        Assert.DoesNotContain("WS01", here.Body);
        Assert.EndsWith(" on FILESRV01", new FailureNotice("FILESRV01", true, task).Body);
    }

    /// <summary>The demo payroll sync runs every 2 minutes and fails every other run.</summary>
    List<FailureNotice> RunFor(MainViewModel vm, TimeSpan span)
    {
        var notices = new List<FailureNotice>();
        vm.RunFailed += notices.Add;
        vm.Monitor.Step();
        for (var end = _now + span; _now < end;)
        {
            _now = _now.AddSeconds(5);
            vm.Monitor.Step();
        }
        return notices;
    }

    [Fact]
    public void The_window_reports_each_failed_run_of_the_demo_payroll_sync()
    {
        using var vm = new MainViewModel(new DemoSchedulerHost(() => _now), EveryTick, () => _now);
        var notices = RunFor(vm, TimeSpan.FromMinutes(9));
        var payroll = notices.Where(n => n.Task.Path == @"\Sync payroll exports").ToList();
        Assert.InRange(payroll.Count, 2, 3);
        Assert.Equal(payroll.Count, payroll.Select(n => n.Task.LastRun).Distinct().Count());
        Assert.All(payroll, n => Assert.Equal("Sign-in refused", n.Result.Short));
        Assert.DoesNotContain(notices, n => n.Task.IsWindowsTask);
    }

    [Fact]
    public void Muting_a_task_or_turning_notifications_off_stops_them()
    {
        var organizer = new MemoryOrganizerStore();
        using (var vm = new MainViewModel(new DemoSchedulerHost(() => _now), EveryTick, () => _now, organizer))
        {
            vm.Monitor.Step();
            vm.SelectedRow = vm.Rows.First(r => r.Path == @"\Sync payroll exports");
            Assert.True(vm.Detail!.CanNotify);
            Assert.Equal("Alerts On", vm.Detail.NotifyText);
            vm.Detail.ToggleNotifyCommand.Execute(null);
            Assert.Equal("Alerts Off", vm.Detail.NotifyText);
            Assert.DoesNotContain(RunFor(vm, TimeSpan.FromMinutes(9)), n => n.Task.Path == @"\Sync payroll exports");
        }
        // Kept with the stars and groups.
        Assert.True(organizer.Load().IsMuted(@"\Sync payroll exports"));

        var settings = new SettingsViewModel(new MemorySettingsStore()) { NotifyFailures = false };
        using var off = new MainViewModel(new DemoSchedulerHost(() => _now), EveryTick, () => _now, settings: settings);
        Assert.Empty(RunFor(off, TimeSpan.FromMinutes(9)));
        off.SelectedRow = off.Rows.First(r => r.Path == @"\Mirror data to NAS");
        Assert.False(off.Detail!.CanNotify);
    }

    [Fact]
    public void Settings_are_saved_as_they_change()
    {
        var store = new MemorySettingsStore();
        var settings = new SettingsViewModel(store);
        Assert.True(settings.NotifyFailures);
        Assert.True(settings.KeepRunningWhenClosed);
        settings.NotifyWindowsTasks = true;
        settings.KeepRunningWhenClosed = false;
        var again = new SettingsViewModel(store);
        Assert.True(again.NotifyWindowsTasks);
        Assert.False(again.KeepRunningWhenClosed);

        Assert.True(again.TakeTrayNote());
        Assert.False(again.TakeTrayNote());
        Assert.False(new SettingsViewModel(store).TakeTrayNote());
    }

    sealed class LockedStartup : IStartup
    {
        public bool IsOn => false;
        public void Set(bool on) => throw new UnauthorizedAccessException("Access is denied.");
    }

    [Fact]
    public void Start_with_windows_that_cannot_be_set_says_so_and_stays_off()
    {
        var settings = new SettingsViewModel(new MemorySettingsStore(), new LockedStartup());
        settings.StartWithWindows = true;
        Assert.False(settings.StartWithWindows);
        Assert.Equal("Couldn't change starting with Windows: Access is denied.", settings.Problem);
    }

    /// <summary>A firewall stand-in: Answer is what the administrator prompt returns, or Fail throws.</summary>
    sealed class FakeRemoteAccess : IRemoteAccess
    {
        public bool IsOn { get; set; }
        public bool Answer { get; set; } = true;
        public Exception? Fail { get; set; }
        public int Calls { get; private set; }

        public System.Threading.Tasks.Task<bool> SetAsync(bool on)
        {
            Calls++;
            if (Fail is not null)
                throw Fail;
            if (Answer)
                IsOn = on;
            return System.Threading.Tasks.Task.FromResult(Answer);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task Allowing_remote_access_changes_the_firewall()
    {
        var remote = new FakeRemoteAccess();
        var settings = new SettingsViewModel(new MemorySettingsStore(), remote: remote);
        Assert.False(settings.AllowRemote);
        settings.AllowRemote = true;
        await settings.RemoteChange;
        Assert.True(remote.IsOn);
        Assert.True(settings.AllowRemote);
        Assert.Null(settings.Problem);
        Assert.True(settings.CanChangeRemote);
    }

    [Fact]
    public async System.Threading.Tasks.Task Declining_the_administrator_prompt_puts_the_switch_back_quietly()
    {
        var remote = new FakeRemoteAccess { Answer = false };
        var settings = new SettingsViewModel(new MemorySettingsStore(), remote: remote);
        settings.AllowRemote = true;
        await settings.RemoteChange;
        Assert.False(settings.AllowRemote);
        Assert.Null(settings.Problem);
    }

    [Fact]
    public async System.Threading.Tasks.Task A_firewall_that_refuses_says_so()
    {
        var remote = new FakeRemoteAccess { Fail = new InvalidOperationException("Windows Firewall didn't take the change (code 5).") };
        var settings = new SettingsViewModel(new MemorySettingsStore(), remote: remote);
        settings.AllowRemote = true;
        await settings.RemoteChange;
        Assert.False(settings.AllowRemote);
        Assert.Equal("Couldn't change whether other computers can manage this one: Windows Firewall didn't take the change (code 5).", settings.Problem);
    }

    [Fact]
    public void Opening_settings_reads_the_firewall_again_without_changing_it()
    {
        var remote = new FakeRemoteAccess();
        var settings = new SettingsViewModel(new MemorySettingsStore(), remote: remote);
        using var vm = new MainViewModel(new DemoSchedulerHost(() => _now), EveryTick, () => _now, settings: settings);
        remote.IsOn = true;   // turned on outside the app
        vm.ToggleSettingsCommand.Execute(null);
        Assert.True(settings.AllowRemote);
        Assert.Equal(0, remote.Calls);
    }

    [Fact]
    public void Settings_and_computers_cards_take_turns()
    {
        using var vm = new MainViewModel(new DemoSchedulerHost(() => _now), EveryTick, () => _now);
        vm.ToggleSettingsCommand.Execute(null);
        Assert.True(vm.ShowSettings);
        vm.ToggleComputersCommand.Execute(null);
        Assert.True(vm.ShowComputers);
        Assert.False(vm.ShowSettings);
    }

    [Fact]
    public void In_the_background_the_monitor_waits_longer_and_wakes_when_shown()
    {
        var host = new DemoSchedulerHost();
        var timing = new MonitorTiming { Tick = TimeSpan.FromMilliseconds(40), BackgroundSlower = 50 };
        using var monitor = new LiveMonitor(host, timing);
        var ticks = 0;
        monitor.Ticked += _ => Interlocked.Increment(ref ticks);
        monitor.InBackground = true;
        monitor.Start();
        Thread.Sleep(500);
        var background = Volatile.Read(ref ticks);
        Assert.InRange(background, 1, 2);   // the first read, then a 2 s wait

        monitor.InBackground = false;       // wakes it straight away
        Thread.Sleep(500);
        Assert.True(Volatile.Read(ref ticks) >= background + 5);
    }
}
