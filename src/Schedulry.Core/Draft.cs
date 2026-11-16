namespace Schedulry.Core;

/// <summary>
/// A trigger in the editor. <see cref="Source"/> is its position in the task as it was saved, so a
/// kind the editor can't express (a custom trigger, a complex event query) is kept exactly as is.
/// </summary>
public sealed record DraftTrigger(TriggerSpec Spec, int? Source = null);

/// <summary>An action in the editor; non-program actions are kept as is through <see cref="Source"/>.</summary>
public sealed record DraftAction(ActionInfo Action, int? Source = null);

/// <summary>
/// A task as the editor will save it: an existing task changed, or a new one. Hosts turn it into
/// a Task Scheduler definition; <see cref="DraftChanges"/> and <see cref="PowerShellScript"/> put it into words.
/// </summary>
public sealed record TaskDraft
{
    /// <summary>The task this draft edits, or null for a new task.</summary>
    public string? OriginalPath { get; init; }
    public required string Folder { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public bool Enabled { get; init; } = true;
    public required string RunAs { get; init; }
    public LogonKind Logon { get; init; } = LogonKind.Interactive;
    public bool HighestPrivileges { get; init; }
    public IReadOnlyList<DraftTrigger> Triggers { get; init; } = [];
    public IReadOnlyList<DraftAction> Actions { get; init; } = [];
    public TaskSettingsInfo Settings { get; init; } = new();

    public string Path => Folder == "\\" ? "\\" + Name : $"{Folder}\\{Name}";
    public bool IsNew => OriginalPath is null;
    public bool IsMoved => OriginalPath is not null && !string.Equals(OriginalPath, Path, StringComparison.OrdinalIgnoreCase);

    /// <summary>Saving this asks Windows for the account's password (it isn't kept anywhere by Schedulry).</summary>
    public bool NeedsPassword => Logon is LogonKind.Password or LogonKind.InteractiveOrPassword;

    public static TaskDraft From(TaskSnapshot task) => new()
    {
        OriginalPath = task.Path,
        Folder = task.Folder,
        Name = task.Name,
        Description = task.Description,
        Enabled = task.Enabled,
        RunAs = task.RunAs,
        Logon = task.Logon,
        HighestPrivileges = task.HighestPrivileges,
        Triggers = task.Triggers.Select((t, i) => new DraftTrigger(t, i)).ToList(),
        Actions = task.Actions.Select((a, i) => new DraftAction(a, i)).ToList(),
        Settings = task.Settings,
    };

    /// <summary>Problems that stop a save, in words. Empty when it can be saved.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(Name))
            problems.Add("Give the task a name.");
        else if (Name.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) >= 0)
            problems.Add("A task's name can't contain \\ / : * ? \" < > or |.");
        if (Actions.Count == 0)
            problems.Add("Add something for the task to do: a program to run.");
        foreach (var (action, i) in Actions.Select((a, i) => (a.Action, i)))
            if (action.Kind == ActionKind.Program && string.IsNullOrWhiteSpace(action.Path))
                problems.Add($"Action {i + 1} needs a program to run.");
        if (string.IsNullOrWhiteSpace(RunAs))
            problems.Add("Choose the account it runs as.");
        foreach (var (trigger, i) in Triggers.Select((t, i) => (t.Spec, i)))
        {
            if (trigger.Kind == TriggerKind.Weekly && trigger.Days == Weekdays.None)
                problems.Add($"Trigger {i + 1} needs at least one day of the week.");
            if (trigger.Kind == TriggerKind.Monthly && trigger.DaysOfMonth == 0 && !trigger.LastDayOfMonth)
                problems.Add($"Trigger {i + 1} needs at least one day of the month.");
            if (trigger.Kind is TriggerKind.Once or TriggerKind.Daily or TriggerKind.Weekly or TriggerKind.Monthly && trigger.Start is null)
                problems.Add($"Trigger {i + 1} needs a start date and time.");
            if (trigger.End is { } end && trigger.Start is { } start && end <= start)
                problems.Add($"Trigger {i + 1} ends before it starts.");
            if (trigger.RepeatEvery is { } every && trigger.RepeatFor is { } span && span < every)
                problems.Add($"Trigger {i + 1} repeats for less time than its interval.");
            if (trigger.Kind == TriggerKind.OnEvent && trigger.EventLog is null && Triggers[i].Source is null)
                problems.Add($"Trigger {i + 1} needs the event log to watch.");
        }
        return problems;
    }
}

/// <summary>What saving a draft will change, as sentences, for the review before saving.</summary>
public static class DraftChanges
{
    public static IReadOnlyList<string> Describe(TaskSnapshot? before, TaskDraft after)
    {
        if (before is null)
            return [$"Create the task \"{after.Name}\" in {FolderText(after.Folder)}."];

        var changes = new List<string>();
        if (!string.Equals(before.Name, after.Name, StringComparison.Ordinal))
            changes.Add($"Rename it from \"{before.Name}\" to \"{after.Name}\".");
        if (!string.Equals(before.Folder, after.Folder, StringComparison.OrdinalIgnoreCase))
            changes.Add($"Move it from {FolderText(before.Folder)} to {FolderText(after.Folder)}. Anything that runs it by its old path will need updating.");
        if ((before.Description ?? "") != (after.Description ?? ""))
            changes.Add(string.IsNullOrWhiteSpace(after.Description) ? "Remove the description." : "Change the description.");
        if (before.Enabled != after.Enabled)
            changes.Add(after.Enabled ? "Turn it on." : "Turn it off.");
        if (!string.Equals(before.RunAs, after.RunAs, StringComparison.OrdinalIgnoreCase) || before.Logon != after.Logon)
            changes.Add($"Run it as {after.RunAs}, {LogonText(after.Logon)} (was {before.RunAs}, {LogonText(before.Logon)}).");
        if (before.HighestPrivileges != after.HighestPrivileges)
            changes.Add(after.HighestPrivileges ? "Run it with full administrator rights." : "Stop running it with full administrator rights.");

        Compare(changes, "trigger", before.Triggers.Select(t => t.Summary).ToList(), after.Triggers.Select(t => t.Spec.Summary).ToList());
        Compare(changes, "action", before.Actions.Select(a => a.Summary).ToList(), after.Actions.Select(a => a.Action.Summary).ToList());

        var (b, a) = (before.Settings, after.Settings);
        if (b.StopAfter != a.StopAfter)
            changes.Add(a.StopAfter is { } limit ? $"Stop it if it runs longer than {TriggerText.Duration(limit)}." : "Never stop it for running too long.");
        if (b.MultipleInstances != a.MultipleInstances)
            changes.Add($"If it's already running when due: {InstancesText(a.MultipleInstances).ToLowerInvariant()}.");
        if (b.RunMissedStart != a.RunMissedStart)
            changes.Add(a.RunMissedStart ? "Run a missed start as soon as possible." : "Skip missed starts.");
        if (b.RestartCount != a.RestartCount || b.RestartInterval != a.RestartInterval)
            changes.Add(a.RestartCount > 0 ? $"If it fails, try again every {TriggerText.Duration(a.RestartInterval)}, up to {a.RestartCount} times." : "Don't try again when it fails.");
        if (b.OnlyOnAcPower != a.OnlyOnAcPower)
            changes.Add(a.OnlyOnAcPower ? "Only run on mains power." : "Run on battery too.");
        if (b.OnlyWhenIdle != a.OnlyWhenIdle)
            changes.Add(a.OnlyWhenIdle ? "Only run when the computer is idle." : "Run whether or not the computer is idle.");
        if (b.OnlyWithNetwork != a.OnlyWithNetwork)
            changes.Add(a.OnlyWithNetwork ? "Only run with a network connection." : "Run without a network connection too.");
        if (b.WakeToRun != a.WakeToRun)
            changes.Add(a.WakeToRun ? "Wake the computer to run it." : "Don't wake the computer to run it.");
        if (b.AllowRunOnDemand != a.AllowRunOnDemand)
            changes.Add(a.AllowRunOnDemand ? "Allow running it by hand." : "Don't allow running it by hand.");
        if (b.Hidden != a.Hidden)
            changes.Add(a.Hidden ? "Hide it in the old Task Scheduler window." : "Show it in the old Task Scheduler window.");
        return changes;
    }

    static void Compare(List<string> changes, string noun, IReadOnlyList<string> before, IReadOnlyList<string> after)
    {
        var count = Math.Max(before.Count, after.Count);
        for (var i = 0; i < count; i++)
        {
            if (i >= before.Count)
                changes.Add($"Add {noun} {i + 1}: {after[i]}.");
            else if (i >= after.Count)
                changes.Add($"Remove {noun} {i + 1}: {before[i]}.");
            else if (before[i] != after[i])
                changes.Add($"Change {noun} {i + 1} to: {after[i]} (was: {before[i]}).");
        }
    }

    public static string FolderText(string folder) => folder == "\\" ? "the top folder" : $"the folder {folder}";

    public static string LogonText(LogonKind logon) => logon switch
    {
        LogonKind.Interactive => "only while signed in",
        LogonKind.Password => "whether signed in or not, with a stored password",
        LogonKind.S4U => "whether signed in or not, without a stored password",
        LogonKind.ServiceAccount => "as a background service",
        LogonKind.Group => "for any signed-in member of the group",
        LogonKind.InteractiveOrPassword => "on the desktop when signed in, otherwise with the stored password",
        _ => "sign-in type unknown",
    };

    public static string InstancesText(string policy) => policy switch
    {
        "Parallel" => "Start another copy alongside it",
        "Queue" => "Start the new run when the current one ends",
        "StopExisting" => "Stop the running copy, then start again",
        _ => "Don't start a new run",
    };
}
