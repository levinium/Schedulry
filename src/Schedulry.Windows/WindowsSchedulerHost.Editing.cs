using System.Runtime.InteropServices;
using Microsoft.Win32.TaskScheduler;
using Schedulry.Core;
using WinAction = Microsoft.Win32.TaskScheduler.Action;
using WinTask = Microsoft.Win32.TaskScheduler.Task;

namespace Schedulry.Windows;

/// <summary>Saving: drafts become Task Scheduler definitions.</summary>
public sealed partial class WindowsSchedulerHost
{
    const int AccessDenied = unchecked((int)0x80070005);
    const int AlreadyExists = unchecked((int)0x800700B7);

    public IReadOnlyList<string> Folders()
    {
        var list = new List<string>();
        var pending = new Stack<TaskFolder>();
        pending.Push(Service.RootFolder);
        while (pending.Count > 0)
        {
            using var folder = pending.Pop();
            list.Add(folder.Path);
            try
            {
                foreach (var sub in folder.SubFolders)
                    pending.Push(sub);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or COMException)
            {
                // A folder this account can't open: listed, but not its children.
            }
        }
        return list.OrderBy(p => p == "\\" ? "" : p, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public string BuildXml(TaskDraft draft)
    {
        using var existing = draft.OriginalPath is null ? null : Find(draft.OriginalPath);
        var definition = Build(draft, existing);
        return definition.XmlText;
    }

    public void Save(TaskDraft draft, string? password)
    {
        if (draft.Problems() is [var first, ..])
            throw new InvalidOperationException(first);
        using var existing = draft.OriginalPath is null ? null : Find(draft.OriginalPath)
            ?? throw new InvalidOperationException("The task no longer exists.");
        var definition = Build(draft, existing);
        // Same place: update in place. New or moved: create, so a task already there isn't overwritten.
        var creation = existing is not null && !draft.IsMoved ? TaskCreation.CreateOrUpdate : TaskCreation.Create;
        Register(draft.Folder, draft.Name, definition, creation, password);
        if (existing is not null && draft.IsMoved)
            Delete(draft.OriginalPath!);
    }

    public void Import(string folder, string name, string xml, string? password)
    {
        var definition = Service.NewTask();
        definition.XmlText = xml;
        Register(folder, name, definition, TaskCreation.Create, password);
    }

    void Register(string folderPath, string name, TaskDefinition definition, TaskCreation creation, string? password)
    {
        var principal = definition.Principal;
        var logon = principal.LogonType;
        var user = logon == TaskLogonType.Group ? principal.GroupId : principal.UserId;
        try
        {
            using var folder = FolderFor(folderPath);
            using var _ = folder.RegisterTaskDefinition(name, definition, creation, user,
                logon is TaskLogonType.Password or TaskLogonType.InteractiveTokenOrPassword ? password : null, logon);
        }
        catch (COMException e) when (e.HResult == AlreadyExists)
        {
            throw new InvalidOperationException($"There's already a task called \"{name}\" in {DraftChanges.FolderText(folderPath)}.");
        }
        catch (COMException e) when (e.HResult == AccessDenied)
        {
            throw new UnauthorizedAccessException("Access is denied.", e);
        }
    }

    /// <summary>The folder, made (with any missing parents) if it doesn't exist yet.</summary>
    TaskFolder FolderFor(string path)
    {
        if (path is "\\" or "")
            return Service.RootFolder;
        try
        {
            return Service.GetFolder(path);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or COMException)
        {
            return Service.RootFolder.CreateFolder(path.TrimStart('\\'), (string?)null, exceptionOnExists: false);
        }
    }

    /// <summary>
    /// The draft as a definition. An existing task's definition is the starting point, so anything
    /// the editor doesn't show (other settings, an unchanged custom trigger) stays as it was.
    /// </summary>
    TaskDefinition Build(TaskDraft draft, WinTask? existing)
    {
        var definition = existing?.Definition ?? Service.NewTask();
        var originalTriggers = existing is null ? [] : definition.Triggers.ToList();
        var originalActions = existing is null ? [] : definition.Actions.ToList();

        var info = definition.RegistrationInfo;
        info.Description = string.IsNullOrWhiteSpace(draft.Description) ? null : draft.Description;
        if (existing is null)
        {
            info.Author = CurrentUser;
            info.Date = DateTime.Now;
        }

        var principal = definition.Principal;
        principal.LogonType = draft.Logon switch
        {
            LogonKind.Password => TaskLogonType.Password,
            LogonKind.S4U => TaskLogonType.S4U,
            LogonKind.ServiceAccount => TaskLogonType.ServiceAccount,
            LogonKind.Group => TaskLogonType.Group,
            LogonKind.InteractiveOrPassword => TaskLogonType.InteractiveTokenOrPassword,
            _ => TaskLogonType.InteractiveToken,
        };
        if (draft.Logon == LogonKind.Group)
        {
            principal.UserId = null;
            principal.GroupId = draft.RunAs;
        }
        else
        {
            principal.GroupId = null;
            principal.UserId = draft.RunAs;
        }
        principal.RunLevel = draft.HighestPrivileges ? TaskRunLevel.Highest : TaskRunLevel.LUA;

        // Triggers: one the person didn't change is copied from the original whole.
        var triggers = draft.Triggers.Select(t =>
            t.Source is int i && i < originalTriggers.Count && (MapTrigger(originalTriggers[i]) == t.Spec || !Buildable(t.Spec))
                ? (Trigger)originalTriggers[i].Clone()
                : FromSpec(t.Spec)).ToList();
        definition.Triggers.Clear();
        foreach (var trigger in triggers)
            definition.Triggers.Add(trigger);

        var actions = draft.Actions.Select(a =>
            a.Source is int i && i < originalActions.Count && (a.Action.Kind != ActionKind.Program || MapAction(originalActions[i]) == a.Action)
                ? (WinAction)originalActions[i].Clone()
                : new ExecAction(a.Action.Path, Blank(a.Action.Arguments), Blank(a.Action.WorkingDirectory))).ToList();
        definition.Actions.Clear();
        foreach (var action in actions)
            definition.Actions.Add(action);

        var s = definition.Settings;
        var d = draft.Settings;
        s.Enabled = draft.Enabled;
        s.ExecutionTimeLimit = d.StopAfter ?? TimeSpan.Zero;
        s.MultipleInstances = Enum.TryParse<TaskInstancesPolicy>(d.MultipleInstances, out var policy) ? policy : TaskInstancesPolicy.IgnoreNew;
        s.StartWhenAvailable = d.RunMissedStart;
        s.RestartCount = d.RestartCount;
        s.RestartInterval = d.RestartCount > 0 ? d.RestartInterval : TimeSpan.Zero;
        s.DisallowStartIfOnBatteries = d.OnlyOnAcPower;
        s.StopIfGoingOnBatteries = d.OnlyOnAcPower;
        s.RunOnlyIfIdle = d.OnlyWhenIdle;
        s.RunOnlyIfNetworkAvailable = d.OnlyWithNetwork;
        s.WakeToRun = d.WakeToRun;
        s.Hidden = d.Hidden;
        s.AllowDemandStart = d.AllowRunOnDemand;
        return definition;
    }

    /// <summary>Kinds the editor can build from scratch; the rest are only ever kept as they were.</summary>
    static bool Buildable(TriggerSpec spec) => spec.Kind switch
    {
        TriggerKind.Custom => false,
        TriggerKind.OnEvent => spec.EventLog is not null,
        _ => true,
    };

    static Trigger FromSpec(TriggerSpec t)
    {
        var start = t.Start ?? DateTime.Now;
        Trigger trigger = t.Kind switch
        {
            TriggerKind.Once => new TimeTrigger(start) { RandomDelay = t.RandomDelay ?? TimeSpan.Zero },
            TriggerKind.Daily => new DailyTrigger((short)Math.Max(1, t.Interval)) { StartBoundary = start, RandomDelay = t.RandomDelay ?? TimeSpan.Zero },
            TriggerKind.Weekly => new WeeklyTrigger((DaysOfTheWeek)(short)t.Days, (short)Math.Max(1, t.Interval)) { StartBoundary = start, RandomDelay = t.RandomDelay ?? TimeSpan.Zero },
            TriggerKind.Monthly => new MonthlyTrigger
            {
                StartBoundary = start,
                DaysOfMonth = Enumerable.Range(1, 31).Where(day => (t.DaysOfMonth & (1u << (day - 1))) != 0).ToArray(),
                MonthsOfYear = t.Months == MonthSet.None ? MonthsOfTheYear.AllMonths : (MonthsOfTheYear)(short)t.Months,
                RunOnLastDayOfMonth = t.LastDayOfMonth,
                RandomDelay = t.RandomDelay ?? TimeSpan.Zero,
            },
            TriggerKind.MonthlyDayOfWeek => new MonthlyDOWTrigger(
                (DaysOfTheWeek)(short)t.Days,
                t.Months == MonthSet.None ? MonthsOfTheYear.AllMonths : (MonthsOfTheYear)(short)t.Months,
                (WhichWeek)(short)(t.Weeks & ~WeekOfMonth.Last))
            {
                StartBoundary = start,
                RunOnLastWeekOfMonth = t.Weeks.HasFlag(WeekOfMonth.Last),
                RandomDelay = t.RandomDelay ?? TimeSpan.Zero,
            },
            TriggerKind.AtLogOn => new LogonTrigger { UserId = t.UserId, Delay = t.Delay ?? TimeSpan.Zero },
            TriggerKind.AtStartup => new BootTrigger { Delay = t.Delay ?? TimeSpan.Zero },
            TriggerKind.OnIdle => new IdleTrigger(),
            TriggerKind.OnRegistration => new RegistrationTrigger { Delay = t.Delay ?? TimeSpan.Zero },
            TriggerKind.OnEvent => new EventTrigger(t.EventLog!, t.EventSource, t.EventId) { Delay = t.Delay ?? TimeSpan.Zero },
            TriggerKind.SessionStateChange => new SessionStateChangeTrigger(
                Enum.TryParse<TaskSessionStateChangeType>(t.SessionChange, out var change) ? change : TaskSessionStateChangeType.SessionLock)
            {
                UserId = t.UserId,
                Delay = t.Delay ?? TimeSpan.Zero,
            },
            _ => throw new InvalidOperationException("This kind of trigger can only be kept as it was."),
        };
        trigger.Enabled = t.Enabled;
        if (t.End is { } end)
            trigger.EndBoundary = end;
        if (t.RepeatEvery is { } every && every > TimeSpan.Zero)
        {
            trigger.Repetition.Interval = every;
            trigger.Repetition.Duration = t.RepeatFor ?? TimeSpan.Zero;
        }
        return trigger;
    }
}
