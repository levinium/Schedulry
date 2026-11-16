using System.Globalization;

namespace Schedulry.Core;

/// <summary>Times as people say them: "2 min ago", "in 3 hours", "Tomorrow 6:00 AM".</summary>
public static class When
{
    public static string Ago(DateTime? time, DateTime now)
    {
        if (time is not { } t)
            return "Never";
        var span = now - t;
        if (span < TimeSpan.Zero)
            return At(t, now);
        if (span.TotalSeconds < 45)
            return "Just now";
        if (span.TotalMinutes < 60)
            return $"{Math.Max(1, (int)Math.Round(span.TotalMinutes))} min ago";
        if (span.TotalHours < 24 && t.Date == now.Date)
            return $"Today {TriggerText.TimeText(t)}";
        if (t.Date == now.Date.AddDays(-1))
            return $"Yesterday {TriggerText.TimeText(t)}";
        if (span.TotalDays < 7)
            return $"{t.ToString("dddd", CultureInfo.CurrentCulture)} {TriggerText.TimeText(t)}";
        return TriggerText.DateText(t);
    }

    public static string Until(DateTime? time, DateTime now)
    {
        if (time is not { } t)
            return "Not scheduled";
        var span = t - now;
        if (span < TimeSpan.Zero)
            return "Due now";
        if (span.TotalSeconds < 60)
            return "In under a minute";
        if (span.TotalMinutes < 60)
            return $"In {(int)Math.Ceiling(span.TotalMinutes)} min";
        return At(t, now);
    }

    /// <summary>"Today 6:00 AM", "Tomorrow 6:00 AM", "Friday 6:00 AM", "3 Mar 2027 6:00 AM".</summary>
    public static string At(DateTime t, DateTime now)
    {
        var time = TriggerText.TimeText(t);
        if (t.Date == now.Date)
            return $"Today {time}";
        if (t.Date == now.Date.AddDays(1))
            return $"Tomorrow {time}";
        if (t.Date > now.Date && t.Date < now.Date.AddDays(7))
            return $"{t.ToString("dddd", CultureInfo.CurrentCulture)} {time}";
        return $"{TriggerText.DateText(t)} {time}";
    }

    /// <summary>A running time on a stopwatch: "0:42", "12:05", "1:02:33"; past a day, "2d 4h".</summary>
    public static string Elapsed(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
            span = TimeSpan.Zero;
        if (span.TotalDays >= 1)
            return span.Hours == 0 ? $"{(int)span.TotalDays}d" : $"{(int)span.TotalDays}d {span.Hours}h";
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes}:{span.Seconds:00}";
    }
}
