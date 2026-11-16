using System.Globalization;
using System.Text;

namespace Schedulry.Core;

public enum TriggerKind
{
    Once,
    Daily,
    Weekly,
    Monthly,
    MonthlyDayOfWeek,
    AtLogOn,
    AtStartup,
    OnIdle,
    OnEvent,
    OnRegistration,
    SessionStateChange,
    Custom,
}

[Flags]
public enum Weekdays
{
    None = 0,
    Sunday = 1,
    Monday = 2,
    Tuesday = 4,
    Wednesday = 8,
    Thursday = 16,
    Friday = 32,
    Saturday = 64,
    Workdays = Monday | Tuesday | Wednesday | Thursday | Friday,
    Weekend = Saturday | Sunday,
    All = Workdays | Weekend,
}

[Flags]
public enum MonthSet
{
    None = 0,
    January = 1, February = 2, March = 4, April = 8, May = 16, June = 32,
    July = 64, August = 128, September = 256, October = 512, November = 1024, December = 2048,
    All = 4095,
}

[Flags]
public enum WeekOfMonth
{
    None = 0,
    First = 1,
    Second = 2,
    Third = 4,
    Fourth = 8,
    Last = 16,
}

/// <summary>
/// One trigger, flattened to value fields only so two triggers compare by content. Every kind uses
/// the fields it needs and leaves the rest at their defaults.
/// </summary>
public sealed record TriggerSpec
{
    public TriggerKind Kind { get; init; }
    public bool Enabled { get; init; } = true;
    public DateTime? Start { get; init; }
    public DateTime? End { get; init; }

    /// <summary>Every N days (Daily) or N weeks (Weekly).</summary>
    public int Interval { get; init; } = 1;
    public Weekdays Days { get; init; }
    public MonthSet Months { get; init; }
    /// <summary>Days of the month as bits: bit 0 is the 1st, bit 30 the 31st.</summary>
    public uint DaysOfMonth { get; init; }
    public bool LastDayOfMonth { get; init; }
    public WeekOfMonth Weeks { get; init; }

    public TimeSpan? RepeatEvery { get; init; }
    /// <summary>How long the repetition lasts; null means indefinitely.</summary>
    public TimeSpan? RepeatFor { get; init; }
    public TimeSpan? Delay { get; init; }
    public TimeSpan? RandomDelay { get; init; }

    /// <summary>At log on / session change: whose; null means any user.</summary>
    public string? UserId { get; init; }
    /// <summary>On an event: the log and, when the query is simple, the source and event ID.</summary>
    public string? EventLog { get; init; }
    public string? EventSource { get; init; }
    public int? EventId { get; init; }
    /// <summary>Session state change: ConsoleConnect, RemoteConnect, SessionLock, SessionUnlock and so on.</summary>
    public string? SessionChange { get; init; }

    public string Summary => TriggerText.Describe(this);
}

/// <summary>
/// Turns triggers into the sentence a person would say: "Every weekday at 6:00 AM, repeating every
/// hour for 12 hours". Uses the current culture for times and dates.
/// </summary>
public static class TriggerText
{
    public static string Describe(TriggerSpec t)
    {
        var text = new StringBuilder(Core(t));
        if (t.Delay is { } delay && delay > TimeSpan.Zero)
            text.Append($", after a {Duration(delay)} delay");
        if (t.RandomDelay is { } random && random > TimeSpan.Zero)
            text.Append($", plus up to {Duration(random)} at random");
        if (t.RepeatEvery is { } every && every > TimeSpan.Zero)
        {
            text.Append($", then every {Duration(every)}");
            text.Append(t.RepeatFor is { } span && span > TimeSpan.Zero ? $" for {Duration(span)}" : " indefinitely");
        }
        if (t.End is { } end)
            text.Append($", until {DateText(end)}");
        if (!t.Enabled)
            text.Append(" (trigger turned off)");
        return text.ToString();
    }

    static string Core(TriggerSpec t) => t.Kind switch
    {
        TriggerKind.Once => t.Start is { } s ? $"Once, on {DateText(s)} at {TimeText(s)}" : "Once",
        TriggerKind.Daily => $"{(t.Interval <= 1 ? "Every day" : $"Every {t.Interval} days")}{At(t.Start)}",
        TriggerKind.Weekly => $"{WeeklyLead(t)}{At(t.Start)}",
        TriggerKind.Monthly => $"{MonthlyDays(t)}{InMonths(t.Months)}{At(t.Start)}",
        TriggerKind.MonthlyDayOfWeek => $"{MonthlyWeeks(t)}{InMonths(t.Months)}{At(t.Start)}",
        TriggerKind.AtLogOn => t.UserId is null ? "When anyone signs in" : $"When {t.UserId} signs in",
        TriggerKind.AtStartup => "When the computer starts",
        TriggerKind.OnIdle => "When the computer becomes idle",
        TriggerKind.OnRegistration => "When the task is created or changed",
        TriggerKind.OnEvent => EventText(t),
        TriggerKind.SessionStateChange => SessionText(t),
        _ => "On a custom trigger",
    };

    static string At(DateTime? start) => start is { } s ? $" at {TimeText(s)}" : "";

    static string WeeklyLead(TriggerSpec t)
    {
        var days = DayList(t.Days);
        if (t.Interval <= 1)
            return t.Days == Weekdays.All ? "Every day" : $"Every {days}";
        return $"Every {Ordinal(t.Interval)} week on {days}";
    }

    /// <summary>"weekday", "Monday and Thursday", "Monday, Wednesday and Friday".</summary>
    public static string DayList(Weekdays days)
    {
        if (days == Weekdays.Workdays)
            return "weekday";
        if (days == Weekdays.Weekend)
            return "Saturday and Sunday";
        // Monday first, as people list a working week.
        Weekdays[] order = [Weekdays.Monday, Weekdays.Tuesday, Weekdays.Wednesday, Weekdays.Thursday, Weekdays.Friday, Weekdays.Saturday, Weekdays.Sunday];
        return JoinAnd(order.Where(d => days.HasFlag(d)).Select(d => d.ToString()).ToList());
    }

    static string MonthlyDays(TriggerSpec t)
    {
        var days = new List<string>();
        for (var i = 0; i < 31; i++)
            if ((t.DaysOfMonth & (1u << i)) != 0)
                days.Add(Ordinal(i + 1));
        if (t.LastDayOfMonth)
            days.Add("last day");
        return days.Count == 0 ? "Monthly" : $"On the {JoinAnd(days)} of every month";
    }

    static string MonthlyWeeks(TriggerSpec t)
    {
        var weeks = new List<string>();
        foreach (var (flag, word) in new[] { (WeekOfMonth.First, "first"), (WeekOfMonth.Second, "second"), (WeekOfMonth.Third, "third"), (WeekOfMonth.Fourth, "fourth"), (WeekOfMonth.Last, "last") })
            if (t.Weeks.HasFlag(flag))
                weeks.Add(word);
        return $"On the {JoinAnd(weeks)} {DayList(t.Days)} of every month";
    }

    static string InMonths(MonthSet months)
    {
        if (months is MonthSet.All or MonthSet.None)
            return "";
        var names = Enumerable.Range(0, 12).Where(i => ((int)months & (1 << i)) != 0)
            .Select(i => CultureInfo.CurrentCulture.DateTimeFormat.GetMonthName(i + 1)).ToList();
        return $" in {JoinAnd(names)}";
    }

    static string EventText(TriggerSpec t)
    {
        if (t.EventLog is null)
            return "When a matching event is logged";
        var what = t.EventId is { } id ? $"event {id}" : "an event";
        var from = t.EventSource is { } source ? $" from {source}" : "";
        return $"When {what}{from} is logged in {t.EventLog}";
    }

    static string SessionText(TriggerSpec t)
    {
        var who = t.UserId is null ? "anyone" : t.UserId;
        return t.SessionChange switch
        {
            "SessionLock" => $"When {who} locks the computer",
            "SessionUnlock" => $"When {who} unlocks the computer",
            "ConsoleConnect" => $"When {who} connects at the computer",
            "ConsoleDisconnect" => $"When {who} disconnects at the computer",
            "RemoteConnect" => $"When {who} connects by Remote Desktop",
            "RemoteDisconnect" => $"When {who} disconnects from Remote Desktop",
            _ => $"When {who}'s session changes",
        };
    }

    public static string TimeText(DateTime time) => time.ToString("t", CultureInfo.CurrentCulture);

    public static string DateText(DateTime time) => time.ToString("d MMM yyyy", CultureInfo.CurrentCulture);

    /// <summary>"1 hour", "90 minutes" stays "1 hour 30 minutes", "3 days".</summary>
    public static string Duration(TimeSpan span)
    {
        var parts = new List<string>();
        if (span.Days > 0)
            parts.Add(Plural(span.Days, "day"));
        if (span.Hours > 0)
            parts.Add(Plural(span.Hours, "hour"));
        if (span.Minutes > 0)
            parts.Add(Plural(span.Minutes, "minute"));
        if (span.Seconds > 0 && span.TotalMinutes < 10)
            parts.Add(Plural(span.Seconds, "second"));
        return parts.Count == 0 ? "0 seconds" : string.Join(" ", parts);
    }

    static string Plural(int n, string unit) => n == 1 ? $"1 {unit}" : $"{n} {unit}s";

    public static string Ordinal(int n)
    {
        var suffix = (n % 100) is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
        return $"{n}{suffix}";
    }

    static string JoinAnd(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        _ => $"{string.Join(", ", items.Take(items.Count - 1))} and {items[^1]}",
    };
}
