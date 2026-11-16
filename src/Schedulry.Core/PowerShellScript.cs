using System.Globalization;
using System.Text;

namespace Schedulry.Core;

/// <summary>
/// The PowerShell that makes the same task with the ScheduledTasks module, for people who script
/// their machines. When something can't be said with New-ScheduledTaskTrigger (monthly, event,
/// idle and session triggers), the script registers the task's XML instead, which always matches.
/// </summary>
public static class PowerShellScript
{
    public static string For(TaskDraft draft, string xml)
    {
        if (draft.Triggers.Any(t => !Expressible(t.Spec)) || draft.Actions.Any(a => a.Action.Kind != ActionKind.Program))
            return FromXml(draft, xml, "Some of this task's triggers or actions can't be written with New-ScheduledTaskTrigger, so the script registers its definition as XML.");

        var s = new StringBuilder();
        s.AppendLine("# Makes (or replaces) this task. Run in PowerShell as administrator when it runs as another account.");
        s.AppendLine("$actions = @(");
        foreach (var (a, i) in draft.Actions.Select((a, i) => (a.Action, i)))
        {
            s.Append($"    New-ScheduledTaskAction -Execute {Quote(a.Path)}");
            if (!string.IsNullOrWhiteSpace(a.Arguments))
                s.Append($" -Argument {Quote(a.Arguments)}");
            if (!string.IsNullOrWhiteSpace(a.WorkingDirectory))
                s.Append($" -WorkingDirectory {Quote(a.WorkingDirectory)}");
            s.AppendLine(i < draft.Actions.Count - 1 ? "," : "");
        }
        s.AppendLine(")");

        s.AppendLine("$triggers = @()");
        foreach (var t in draft.Triggers.Select(t => t.Spec))
        {
            s.AppendLine($"$t = {TriggerCommand(t)}");
            if (t.RepeatEvery is { } every)
            {
                // Repetition comes from a -Once trigger's settings, copied onto this one.
                var duration = t.RepeatFor is { } span ? $" -RepetitionDuration {Span(span)}" : "";
                s.AppendLine($"$t.Repetition = (New-ScheduledTaskTrigger -Once -At {Time(t.Start ?? DateTime.Today)} -RepetitionInterval {Span(every)}{duration}).Repetition");
            }
            if (t.Delay is { } delay)
                s.AppendLine($"$t.Delay = '{System.Xml.XmlConvert.ToString(delay)}'");
            if (t.End is { } end)
                s.AppendLine($"$t.EndBoundary = '{end:yyyy-MM-ddTHH:mm:ss}'");
            if (!t.Enabled)
                s.AppendLine("$t.Enabled = $false");
            s.AppendLine("$triggers += $t");
        }

        var logon = draft.Logon switch
        {
            LogonKind.Password => "Password",
            LogonKind.S4U => "S4U",
            LogonKind.ServiceAccount => "ServiceAccount",
            LogonKind.Group => "Group",
            LogonKind.InteractiveOrPassword => "InteractiveOrPassword",
            _ => "Interactive",
        };
        var level = draft.HighestPrivileges ? " -RunLevel Highest" : "";
        s.AppendLine(draft.Logon == LogonKind.Group
            ? $"$principal = New-ScheduledTaskPrincipal -GroupId {Quote(draft.RunAs)}{level}"
            : $"$principal = New-ScheduledTaskPrincipal -UserId {Quote(draft.RunAs)} -LogonType {logon}{level}");

        var set = draft.Settings;
        var settings = new StringBuilder("$settings = New-ScheduledTaskSettingsSet");
        settings.Append($" -MultipleInstances {set.MultipleInstances}");
        settings.Append($" -ExecutionTimeLimit {Span(set.StopAfter ?? TimeSpan.Zero)}");
        if (set.RunMissedStart)
            settings.Append(" -StartWhenAvailable");
        if (set.RestartCount > 0)
            settings.Append($" -RestartCount {set.RestartCount} -RestartInterval {Span(set.RestartInterval)}");
        if (!set.OnlyOnAcPower)
            settings.Append(" -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries");
        if (set.OnlyWhenIdle)
            settings.Append(" -RunOnlyIfIdle");
        if (set.OnlyWithNetwork)
            settings.Append(" -RunOnlyIfNetworkAvailable");
        if (set.WakeToRun)
            settings.Append(" -WakeToRun");
        if (set.Hidden)
            settings.Append(" -Hidden");
        if (!set.AllowRunOnDemand)
            settings.Append(" -DisallowDemandStart");
        if (!draft.Enabled)
            settings.Append(" -Disable");
        s.AppendLine(settings.ToString());

        s.Append($"Register-ScheduledTask -TaskName {Quote(draft.Name)} -TaskPath {Quote(TaskPath(draft.Folder))} -Action $actions -Trigger $triggers -Principal $principal -Settings $settings");
        if (!string.IsNullOrWhiteSpace(draft.Description))
            s.Append($" -Description {Quote(draft.Description)}");
        if (draft.NeedsPassword)
            s.Append(" -User $principal.UserId -Password (Read-Host 'Password for the account')");
        s.AppendLine(" -Force");
        return s.ToString();
    }

    static string FromXml(TaskDraft draft, string xml, string why)
    {
        var s = new StringBuilder();
        s.AppendLine($"# {why}");
        s.AppendLine("$xml = @'");
        s.AppendLine(xml.Trim());
        s.AppendLine("'@");
        s.Append($"Register-ScheduledTask -TaskName {Quote(draft.Name)} -TaskPath {Quote(TaskPath(draft.Folder))} -Xml $xml");
        if (draft.NeedsPassword)
            s.Append($" -User {Quote(draft.RunAs)} -Password (Read-Host 'Password for the account')");
        s.AppendLine(" -Force");
        return s.ToString();
    }

    static bool Expressible(TriggerSpec t) => t.Kind switch
    {
        TriggerKind.Once or TriggerKind.Daily or TriggerKind.AtStartup or TriggerKind.AtLogOn => true,
        TriggerKind.Weekly => true,
        _ => false,
    } && t.RandomDelay is null;

    static string TriggerCommand(TriggerSpec t) => t.Kind switch
    {
        TriggerKind.Once => $"New-ScheduledTaskTrigger -Once -At {Time(t.Start!.Value)}",
        TriggerKind.Daily => $"New-ScheduledTaskTrigger -Daily -DaysInterval {t.Interval} -At {Time(t.Start!.Value)}",
        TriggerKind.Weekly => $"New-ScheduledTaskTrigger -Weekly -WeeksInterval {t.Interval} -DaysOfWeek {string.Join(',', DayNames(t.Days))} -At {Time(t.Start!.Value)}",
        TriggerKind.AtStartup => "New-ScheduledTaskTrigger -AtStartup",
        TriggerKind.AtLogOn => t.UserId is null ? "New-ScheduledTaskTrigger -AtLogOn" : $"New-ScheduledTaskTrigger -AtLogOn -User {Quote(t.UserId)}",
        _ => throw new InvalidOperationException("Not expressible"),
    };

    static IEnumerable<string> DayNames(Weekdays days) =>
        Enum.GetValues<DayOfWeek>().Where(d => days.HasFlag((Weekdays)(1 << (int)d))).Select(d => d.ToString());

    /// <summary>The path Register-ScheduledTask wants: "\", or "\Folder\" with both slashes.</summary>
    public static string TaskPath(string folder) => folder == "\\" ? "\\" : folder.TrimEnd('\\') + "\\";

    static string Time(DateTime time) => $"'{time.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}'";

    static string Span(TimeSpan span) => $"(New-TimeSpan -Seconds {(long)span.TotalSeconds})";

    /// <summary>
    /// A single-quoted PowerShell string: nothing inside expands. PowerShell also ends a string at
    /// typographic single quotes (‘ ’ ‚ ‛), so those are doubled too.
    /// </summary>
    public static string Quote(string text)
    {
        var s = new StringBuilder("'");
        foreach (var c in text)
        {
            s.Append(c);
            if (c is '\'' or '‘' or '’' or '‚' or '‛')
                s.Append(c);
        }
        return s.Append('\'').ToString();
    }
}
