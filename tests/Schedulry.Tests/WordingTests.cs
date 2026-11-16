using System.Globalization;
using Schedulry.Core;

namespace Schedulry.Tests;

public class WordingTests
{
    public WordingTests() => CultureInfo.CurrentCulture = new CultureInfo("en-US");

    static readonly DateTime SixAm = new(2026, 1, 5, 6, 0, 0);

    [Fact]
    public void Weekday_trigger_with_repetition_reads_as_a_sentence()
    {
        var trigger = new TriggerSpec { Kind = TriggerKind.Weekly, Days = Weekdays.Workdays, Start = SixAm, RepeatEvery = TimeSpan.FromHours(1), RepeatFor = TimeSpan.FromHours(12) };
        Assert.Equal("Every weekday at 6:00 AM, then every 1 hour for 12 hours", trigger.Summary);
    }

    [Fact]
    public void Daily_every_other_day()
    {
        Assert.Equal("Every 2 days at 6:00 AM", new TriggerSpec { Kind = TriggerKind.Daily, Interval = 2, Start = SixAm }.Summary);
    }

    [Fact]
    public void Monthly_days_and_last_day()
    {
        var trigger = new TriggerSpec { Kind = TriggerKind.Monthly, DaysOfMonth = 1u | (1u << 14), LastDayOfMonth = true, Months = MonthSet.All, Start = SixAm };
        Assert.Equal("On the 1st, 15th and last day of every month at 6:00 AM", trigger.Summary);
    }

    [Fact]
    public void First_saturday_in_some_months()
    {
        var trigger = new TriggerSpec { Kind = TriggerKind.MonthlyDayOfWeek, Weeks = WeekOfMonth.First, Days = Weekdays.Saturday, Months = MonthSet.January | MonthSet.July, Start = SixAm };
        Assert.Equal("On the first Saturday of every month in January and July at 6:00 AM", trigger.Summary);
    }

    [Fact]
    public void Event_and_logon_and_off()
    {
        Assert.Equal("When event 153 from disk is logged in System", new TriggerSpec { Kind = TriggerKind.OnEvent, EventLog = "System", EventSource = "disk", EventId = 153 }.Summary);
        Assert.Equal("When anyone signs in, after a 2 minutes delay (trigger turned off)",
            new TriggerSpec { Kind = TriggerKind.AtLogOn, Delay = TimeSpan.FromMinutes(2), Enabled = false }.Summary);
    }

    [Theory]
    [InlineData(0, "Completed successfully", "Succeeded", TaskOutcome.Succeeded)]
    [InlineData(0x41301, "Still running", "Still running", TaskOutcome.Running)]
    [InlineData(1, "The program reported an error (exit code 1)", "Exit code 1", TaskOutcome.Failed)]
    [InlineData(unchecked((int)0x80070569), "Sign-in refused: the account lacks the \"Log on as a batch job\" right", "Sign-in refused", TaskOutcome.Failed)]
    [InlineData(unchecked((int)0x8004131F), "Skipped: the previous run was still going", "Skipped (still running)", TaskOutcome.Warning)]
    public void Result_codes_read_as_words(int code, string title, string shortTitle, TaskOutcome outcome)
    {
        var text = ResultCodes.Describe(code);
        Assert.Equal(title, text.Title);
        Assert.Equal(shortTitle, text.Short);
        Assert.Equal(outcome, text.Outcome);
    }

    [Fact]
    public void Unknown_windows_errors_use_the_system_message()
    {
        var text = ResultCodes.Describe(unchecked((int)0x80070020));   // sharing violation
        Assert.Equal(TaskOutcome.Failed, text.Outcome);
        Assert.Contains("process", text.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("0x80070020", text.Code);
    }

    [Fact]
    public void Relative_times()
    {
        var now = new DateTime(2026, 1, 7, 10, 0, 0);   // a Wednesday
        Assert.Equal("Just now", When.Ago(now.AddSeconds(-10), now));
        Assert.Equal("5 min ago", When.Ago(now.AddMinutes(-5), now));
        Assert.Equal("Yesterday 11:00 PM", When.Ago(now.Date.AddHours(-1), now));
        Assert.Equal("In 3 min", When.Until(now.AddMinutes(2.5), now));
        Assert.Equal("Tomorrow 6:00 AM", When.Until(now.Date.AddDays(1).AddHours(6), now));
        Assert.Equal("Friday 6:00 AM", When.Until(now.Date.AddDays(2).AddHours(6), now));
        Assert.Equal("1:02:03", When.Elapsed(new TimeSpan(1, 2, 3)));
        Assert.Equal("0:42", When.Elapsed(TimeSpan.FromSeconds(42)));
    }
}
