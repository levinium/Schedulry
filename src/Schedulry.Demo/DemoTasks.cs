using Schedulry.Core;

namespace Schedulry.Demo;

/// <summary>The demo computer's tasks. Fictional names, accounts and paths only (Contoso).</summary>
static class DemoTasks
{
    public sealed record Seed(TaskSnapshot Task, TimeSpan? Cycle = null, TimeSpan Duration = default, TimeSpan Offset = default, int FailEvery = 0, int FailCode = 1);

    const string Me = @"CONTOSO\avery.lee";
    const string Backup = @"CONTOSO\svc-backup";
    const string Reports = @"CONTOSO\svc-reports";
    const string System = "SYSTEM";

    static TimeSpan S(int seconds) => TimeSpan.FromSeconds(seconds);

    public static IEnumerable<Seed> Build(DateTime now)
    {
        var today = now.Date;
        DateTime At(int hour, int minute = 0) => today.AddDays(-30).AddHours(hour).AddMinutes(minute);
        TriggerSpec Daily(int hour, int minute = 0) => new() { Kind = TriggerKind.Daily, Start = At(hour, minute) };
        TriggerSpec Weekly(Weekdays days, int hour, int minute = 0) => new() { Kind = TriggerKind.Weekly, Days = days, Start = At(hour, minute) };
        ActionInfo Ps(string script, string? args = null) =>
            new(ActionKind.Program, @"C:\Program Files\PowerShell\7\pwsh.exe", $"-NoProfile -File \"{script}\"{(args is null ? "" : " " + args)}", @"C:\Scripts");

        // ---- Simulated: these start and finish on their own while the demo runs. ----
        yield return new Seed(new TaskSnapshot
        {
            Path = @"\Printer queue watchdog",
            Description = "Restarts the print spooler when jobs have been stuck for more than five minutes.",
            Author = Me,
            RunAs = System,
            Logon = LogonKind.ServiceAccount,
            HighestPrivileges = true,
            Triggers = [new TriggerSpec { Kind = TriggerKind.AtStartup, Start = At(0), RepeatEvery = TimeSpan.FromMinutes(1) }],
            Actions = [Ps(@"C:\Scripts\Watch-PrintQueue.ps1")],
            Settings = new TaskSettingsInfo { StopAfter = TimeSpan.FromMinutes(10), RunMissedStart = true },
        }, Cycle: S(60), Duration: S(6), Offset: S(3));

        yield return new Seed(new TaskSnapshot
        {
            Path = @"\Sync payroll exports",
            Description = "Copies the payroll system's export files to the finance share.",
            Author = Me,
            RunAs = Me,
            Logon = LogonKind.S4U,
            Triggers = [Weekly(Weekdays.Workdays, 6) with { RepeatEvery = TimeSpan.FromMinutes(2), RepeatFor = TimeSpan.FromHours(12) }],
            Actions = [Ps(@"C:\Scripts\Sync-PayrollExports.ps1", "-Target \\\\files01\\finance")],
            Settings = new TaskSettingsInfo { StopAfter = TimeSpan.FromMinutes(30), RunMissedStart = true },
        }, Cycle: S(120), Duration: S(10), Offset: S(14), FailEvery: 2, FailCode: unchecked((int)0x80070569));

        yield return new Seed(new TaskSnapshot
        {
            Path = @"\Maintenance\Update antivirus definitions",
            Author = Me,
            RunAs = System,
            Logon = LogonKind.ServiceAccount,
            HighestPrivileges = true,
            Triggers = [Daily(0) with { RepeatEvery = TimeSpan.FromMinutes(3) }],
            Actions = [new ActionInfo(ActionKind.Program, @"C:\Program Files\Contoso Defender\update.exe", "/quiet")],
            Settings = new TaskSettingsInfo { StopAfter = TimeSpan.FromHours(1), RunMissedStart = true, OnlyWithNetwork = true },
        }, Cycle: S(180), Duration: S(15), Offset: S(31));

        yield return new Seed(new TaskSnapshot
        {
            Path = @"\Mirror data to NAS",
            Description = "Mirrors D:\\Data to the backup NAS. Logs to D:\\Logs\\backup.",
            Author = Me,
            RunAs = Backup,
            Logon = LogonKind.Password,
            HighestPrivileges = true,
            Triggers = [Daily(0) with { RepeatEvery = TimeSpan.FromMinutes(5) }],
            Actions = [new ActionInfo(ActionKind.Program, @"C:\Windows\System32\robocopy.exe", @"D:\Data \\nas01\backup\ws01 /MIR /R:2 /W:5 /LOG+:D:\Logs\backup\robocopy.log")],
            Settings = new TaskSettingsInfo { StopAfter = TimeSpan.FromHours(6), RunMissedStart = true, WakeToRun = true, RestartCount = 3, RestartInterval = TimeSpan.FromMinutes(10) },
        }, Cycle: S(300), Duration: S(40), Offset: S(50), FailEvery: 7, FailCode: 16);

        // ---- Static: believable last and next runs. ----
        yield return new Seed(new TaskSnapshot
        {
            Path = @"\Clear temp folders",
            Author = Me,
            RunAs = Me,
            Logon = LogonKind.Interactive,
            Triggers = [Weekly(Weekdays.Sunday, 3)],
            Actions = [Ps(@"C:\Scripts\Clear-TempFolders.ps1")],
            LastRun = LastWeekday(now, DayOfWeek.Sunday).AddHours(3),
            LastResult = 0,
        });

        yield return new Seed(new TaskSnapshot
        {
            Path = @"\Users\Lock screen reminder",
            Description = "Shows a reminder to lock the screen when stepping away.",
            Author = Me,
            RunAs = Me,
            Logon = LogonKind.Interactive,
            Triggers = [new TriggerSpec { Kind = TriggerKind.AtLogOn, UserId = Me, Delay = TimeSpan.FromMinutes(2) }],
            Actions = [new ActionInfo(ActionKind.Program, @"C:\Scripts\reminder.exe")],
            LastRun = today.AddHours(8).AddMinutes(4),
            LastResult = 0,
        });

        yield return new Seed(new TaskSnapshot
        {
            Path = @"\Reports\Monthly usage report",
            Author = Reports,
            RunAs = Reports,
            Logon = LogonKind.Password,
            Triggers = [new TriggerSpec { Kind = TriggerKind.Monthly, DaysOfMonth = 1, Months = MonthSet.All, Start = At(7) }],
            Actions = [Ps(@"C:\Scripts\Reports\New-UsageReport.ps1", "-Month Previous")],
            LastRun = new DateTime(now.Year, now.Month, 1, 7, 0, 0),
            LastResult = 0,
        });

        yield return new Seed(new TaskSnapshot
        {
            Path = @"\Reports\Weekly disk space summary",
            Author = Reports,
            RunAs = Reports,
            Logon = LogonKind.Password,
            Triggers = [Weekly(Weekdays.Monday, 8)],
            Actions = [Ps(@"C:\Scripts\Reports\Send-DiskSummary.ps1")],
            LastRun = LastWeekday(now, DayOfWeek.Monday).AddHours(8),
            LastResult = unchecked((int)0x80070002),
        });

        yield return new Seed(new TaskSnapshot
        {
            Path = @"\Reports\Daily sales digest",
            Description = "Paused while the sales system moves to its new server.",
            Author = Reports,
            RunAs = Reports,
            Logon = LogonKind.Password,
            Enabled = false,
            Triggers = [Weekly(Weekdays.Workdays, 7, 30)],
            Actions = [Ps(@"C:\Scripts\Reports\Send-SalesDigest.ps1")],
            LastRun = today.AddDays(-9).AddHours(7).AddMinutes(30),
            LastResult = 0,
        });

        yield return new Seed(new TaskSnapshot
        {
            Path = @"\Maintenance\Rotate web logs",
            Author = Me,
            RunAs = System,
            Logon = LogonKind.ServiceAccount,
            Triggers = [Daily(0, 15)],
            Actions = [Ps(@"C:\Scripts\Rotate-Logs.ps1", "-Path C:\\inetpub\\logs -KeepDays 30")],
            LastRun = today.AddMinutes(15),
            LastResult = 0,
        });

        yield return new Seed(new TaskSnapshot
        {
            Path = @"\Maintenance\Check certificate expiry",
            Author = Me,
            RunAs = Me,
            Logon = LogonKind.S4U,
            Triggers = [Daily(9)],
            Actions = [Ps(@"C:\Scripts\Test-CertificateExpiry.ps1", "-WarnDays 30")],
            LastRun = today.AddDays(now.Hour >= 9 ? 0 : -1).AddHours(9),
            LastResult = 0,
        });

        yield return new Seed(new TaskSnapshot
        {
            Path = @"\Maintenance\Defragment data drive",
            Author = Me,
            RunAs = System,
            Logon = LogonKind.ServiceAccount,
            HighestPrivileges = true,
            Triggers = [new TriggerSpec { Kind = TriggerKind.MonthlyDayOfWeek, Weeks = WeekOfMonth.First, Days = Weekdays.Saturday, Months = MonthSet.All, Start = At(2) }],
            Actions = [new ActionInfo(ActionKind.Program, @"C:\Windows\System32\defrag.exe", "D: /O")],
            Settings = new TaskSettingsInfo { OnlyOnAcPower = true, OnlyWhenIdle = true, StopAfter = TimeSpan.FromHours(3) },
            LastRun = today.AddDays(-12).AddHours(2),
            LastResult = 0x41306,
        });

        yield return new Seed(new TaskSnapshot
        {
            Path = @"\Maintenance\Restart print spooler once",
            Description = "One-off restart after the driver update.",
            Author = Me,
            RunAs = System,
            Logon = LogonKind.ServiceAccount,
            Triggers = [new TriggerSpec { Kind = TriggerKind.Once, Start = today.AddDays(2).AddHours(22) }],
            Actions = [new ActionInfo(ActionKind.Program, @"C:\Windows\System32\net.exe", "stop spooler && net start spooler")],
        });

        yield return new Seed(new TaskSnapshot
        {
            Path = @"\Maintenance\Alert on disk errors",
            Author = Me,
            RunAs = Me,
            Logon = LogonKind.S4U,
            Triggers = [new TriggerSpec { Kind = TriggerKind.OnEvent, EventLog = "System", EventSource = "disk", EventId = 153 }],
            Actions = [Ps(@"C:\Scripts\Send-DiskAlert.ps1")],
            LastRun = today.AddDays(-21).AddHours(14).AddMinutes(12),
            LastResult = 0,
        });

        // ---- Windows' own tasks (hidden from the list until "Show Windows tasks" is on). ----
        TaskSnapshot Windows(string path, TriggerSpec trigger, string program, string? args, int dayAgo, int? result = 0, bool enabled = true) => new()
        {
            Path = path,
            Author = "Microsoft Corporation",
            RunAs = System,
            Logon = LogonKind.ServiceAccount,
            Enabled = enabled,
            Triggers = [trigger],
            Actions = [new ActionInfo(ActionKind.Program, program, args)],
            LastRun = today.AddDays(-dayAgo).AddHours(3).AddMinutes(17),
            LastResult = result,
        };
        yield return new Seed(Windows(@"\Microsoft\Windows\Defrag\ScheduledDefrag", Weekly(Weekdays.Wednesday, 1), @"%windir%\system32\defrag.exe", "-c -h -o -$", 4));
        yield return new Seed(Windows(@"\Microsoft\Windows\Time Synchronization\SynchronizeTime", Weekly(Weekdays.Sunday, 1), @"%windir%\system32\sc.exe", "start w32time task_started", 2));
        yield return new Seed(Windows(@"\Microsoft\Windows\Diagnosis\Scheduled", Weekly(Weekdays.Tuesday, 1), @"%windir%\system32\sdiagschd.exe", null, 6));
        yield return new Seed(Windows(@"\Microsoft\Windows\Maintenance\WinSAT", Weekly(Weekdays.Sunday, 1), @"%windir%\system32\winsat.exe", "formal", 40, enabled: false));
        yield return new Seed(Windows(@"\Microsoft\Windows\UpdateOrchestrator\Schedule Scan", Daily(4) with { RepeatEvery = TimeSpan.FromHours(22) }, @"%systemroot%\system32\usoclient.exe", "StartScan", 0));
        yield return new Seed(Windows(@"\Microsoft\Windows\Windows Error Reporting\QueueReporting", new TriggerSpec { Kind = TriggerKind.AtLogOn }, @"%windir%\system32\wermgr.exe", "-upload", 1, unchecked((int)0x80070005)));
    }

    static DateTime LastWeekday(DateTime now, DayOfWeek day)
    {
        var date = now.Date;
        while (date.DayOfWeek != day || date == now.Date)
            date = date.AddDays(-1);
        return date;
    }
}
