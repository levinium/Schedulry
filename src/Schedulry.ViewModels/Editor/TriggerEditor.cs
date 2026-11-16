using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Schedulry.Core;

namespace Schedulry.ViewModels.Editor;

/// <summary>
/// One trigger in the editor. Shows only the fields its kind uses. Anything the editor has no
/// field for (months, a random delay) is carried over from the trigger as it was.
/// </summary>
public sealed partial class TriggerEditor : EditorPart
{
    readonly TriggerSpec _original;
    readonly Action<TriggerEditor> _remove;

    public static IReadOnlyList<Choice<TriggerKind>> Kinds { get; } =
    [
        new(TriggerKind.Once, "One time"),
        new(TriggerKind.Daily, "Daily"),
        new(TriggerKind.Weekly, "Weekly"),
        new(TriggerKind.Monthly, "Monthly, on dates"),
        new(TriggerKind.MonthlyDayOfWeek, "Monthly, on a weekday"),
        new(TriggerKind.AtStartup, "When the computer starts"),
        new(TriggerKind.AtLogOn, "When someone signs in"),
        new(TriggerKind.OnIdle, "When the computer is idle"),
        new(TriggerKind.OnEvent, "When an event is logged"),
        new(TriggerKind.SessionStateChange, "On a lock, unlock or connection"),
        new(TriggerKind.OnRegistration, "When the task is saved"),
    ];

    public static IReadOnlyList<Choice<string>> SessionChanges { get; } =
    [
        new("SessionLock", "Locks the computer"),
        new("SessionUnlock", "Unlocks the computer"),
        new("ConsoleConnect", "Connects at the computer"),
        new("ConsoleDisconnect", "Disconnects at the computer"),
        new("RemoteConnect", "Connects by Remote Desktop"),
        new("RemoteDisconnect", "Disconnects from Remote Desktop"),
    ];

    public TriggerEditor(DraftTrigger? trigger, Action<TriggerEditor> remove)
    {
        _remove = remove;
        var spec = trigger?.Spec ?? new TriggerSpec { Kind = TriggerKind.Daily, Start = DateTime.Today.AddDays(1).AddHours(9) };
        _original = spec;
        Source = trigger?.Source;
        IsKept = spec.Kind == TriggerKind.Custom || (spec.Kind == TriggerKind.OnEvent && spec.EventLog is null && trigger?.Source is not null);
        Kind = Kinds.FirstOrDefault(k => k.Value == spec.Kind) ?? Kinds[1];
        Enabled = spec.Enabled;
        var start = spec.Start ?? DateTime.Today.AddDays(1).AddHours(9);
        StartDate = start.Date;
        StartTime = start.TimeOfDay;
        Interval = Math.Max(1, spec.Interval);
        Monday = spec.Days.HasFlag(Weekdays.Monday);
        Tuesday = spec.Days.HasFlag(Weekdays.Tuesday);
        Wednesday = spec.Days.HasFlag(Weekdays.Wednesday);
        Thursday = spec.Days.HasFlag(Weekdays.Thursday);
        Friday = spec.Days.HasFlag(Weekdays.Friday);
        Saturday = spec.Days.HasFlag(Weekdays.Saturday);
        Sunday = spec.Days.HasFlag(Weekdays.Sunday);
        if (spec.Kind != TriggerKind.Weekly && spec.Kind != TriggerKind.MonthlyDayOfWeek && spec.Days == Weekdays.None)
            Monday = true;   // a sensible default when switching kind
        DaysOfMonth = string.Join(", ", Enumerable.Range(1, 31).Where(d => (spec.DaysOfMonth & (1u << (d - 1))) != 0));
        if (DaysOfMonth.Length == 0 && !spec.LastDayOfMonth)
            DaysOfMonth = "1";
        LastDayOfMonth = spec.LastDayOfMonth;
        First = spec.Weeks.HasFlag(WeekOfMonth.First) || spec.Weeks == WeekOfMonth.None;
        Second = spec.Weeks.HasFlag(WeekOfMonth.Second);
        Third = spec.Weeks.HasFlag(WeekOfMonth.Third);
        Fourth = spec.Weeks.HasFlag(WeekOfMonth.Fourth);
        Last = spec.Weeks.HasFlag(WeekOfMonth.Last);
        Repeat = spec.RepeatEvery is not null;
        RepeatEvery = new DurationEditor(spec.RepeatEvery ?? TimeSpan.FromHours(1));
        RepeatForever = spec.RepeatEvery is not null && spec.RepeatFor is null;
        RepeatFor = new DurationEditor(spec.RepeatFor ?? TimeSpan.FromDays(1));
        HasDelay = spec.Delay is not null;
        Delay = new DurationEditor(spec.Delay ?? TimeSpan.FromMinutes(1));
        Expires = spec.End is not null;
        var end = spec.End ?? start.AddYears(1);
        EndDate = end.Date;
        EndTime = end.TimeOfDay;
        AnyUser = spec.UserId is null;
        UserId = spec.UserId ?? "";
        EventLog = spec.EventLog ?? "System";
        EventSource = spec.EventSource ?? "";
        EventId = spec.EventId?.ToString() ?? "";
        SessionChange = SessionChanges.FirstOrDefault(c => c.Value == spec.SessionChange) ?? SessionChanges[0];
    }

    public void Bind(Action changed)
    {
        Attach(changed);
        RepeatEvery.Attach(Refresh);
        RepeatFor.Attach(Refresh);
        Delay.Attach(Refresh);
        void Refresh()
        {
            OnPropertyChanged(nameof(Summary));
            changed();
        }
    }

    public int? Source { get; }
    /// <summary>A kind the editor can't build (a custom trigger, a complex event filter): kept exactly as it was.</summary>
    public bool IsKept { get; }
    public bool IsEditable => !IsKept;
    public int Number { get; set; }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasStart), nameof(IsDaily), nameof(IsWeekly), nameof(IsMonthly), nameof(IsMonthlyWeekday),
        nameof(HasUser), nameof(IsEvent), nameof(IsSession), nameof(CanDelay), nameof(CanRepeat), nameof(IntervalUnit), nameof(Summary))]
    public partial Choice<TriggerKind> Kind { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Summary))] public partial bool Enabled { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Summary))] public partial DateTime? StartDate { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Summary))] public partial TimeSpan? StartTime { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Summary))] public partial decimal? Interval { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Summary))] public partial bool Monday { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Summary))] public partial bool Tuesday { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Summary))] public partial bool Wednesday { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Summary))] public partial bool Thursday { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Summary))] public partial bool Friday { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Summary))] public partial bool Saturday { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Summary))] public partial bool Sunday { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Summary))] public partial string DaysOfMonth { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Summary))] public partial bool LastDayOfMonth { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Summary))] public partial bool First { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Summary))] public partial bool Second { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Summary))] public partial bool Third { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Summary))] public partial bool Fourth { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Summary))] public partial bool Last { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Summary))] public partial bool Repeat { get; set; }
    public DurationEditor RepeatEvery { get; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Summary), nameof(RepeatLimited))] public partial bool RepeatForever { get; set; }
    public DurationEditor RepeatFor { get; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Summary))] public partial bool HasDelay { get; set; }
    public DurationEditor Delay { get; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Summary))] public partial bool Expires { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Summary))] public partial DateTime? EndDate { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Summary))] public partial TimeSpan? EndTime { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Summary), nameof(SpecificUser))] public partial bool AnyUser { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Summary))] public partial string UserId { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Summary))] public partial string EventLog { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Summary))] public partial string EventSource { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Summary))] public partial string EventId { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Summary))] public partial Choice<string> SessionChange { get; set; }

    TriggerKind K => Kind.Value;
    public bool HasStart => IsEditable && K is TriggerKind.Once or TriggerKind.Daily or TriggerKind.Weekly or TriggerKind.Monthly or TriggerKind.MonthlyDayOfWeek;
    public bool IsDaily => IsEditable && K == TriggerKind.Daily;
    public bool IsWeekly => IsEditable && K == TriggerKind.Weekly;
    public bool IsMonthly => IsEditable && K == TriggerKind.Monthly;
    public bool IsMonthlyWeekday => IsEditable && K == TriggerKind.MonthlyDayOfWeek;
    public bool HasWeekdays => IsWeekly || IsMonthlyWeekday;
    public bool HasUser => IsEditable && K is TriggerKind.AtLogOn or TriggerKind.SessionStateChange;
    public bool SpecificUser
    {
        get => !AnyUser;
        set => AnyUser = !value;
    }
    public bool IsEvent => IsEditable && K == TriggerKind.OnEvent;
    public bool IsSession => IsEditable && K == TriggerKind.SessionStateChange;
    public bool CanDelay => IsEditable && K is TriggerKind.AtLogOn or TriggerKind.AtStartup or TriggerKind.OnEvent or TriggerKind.OnRegistration or TriggerKind.SessionStateChange;
    public bool CanRepeat => IsEditable && K != TriggerKind.OnIdle;
    public bool RepeatLimited => !RepeatForever;
    public string IntervalUnit => K == TriggerKind.Weekly ? "weeks" : "days";

    partial void OnKindChanged(Choice<TriggerKind> value) => OnPropertyChanged(nameof(HasWeekdays));

    /// <summary>The trigger in a sentence, as it stands now.</summary>
    public string Summary => ToSpec().Summary;

    [RelayCommand]
    void Remove() => _remove(this);

    public DraftTrigger ToDraft() => new(IsKept ? _original : ToSpec(), Source);

    public TriggerSpec ToSpec()
    {
        if (IsKept)
            return _original;
        var start = (StartDate ?? DateTime.Today) + (StartTime ?? TimeSpan.Zero);
        var days = (Monday ? Weekdays.Monday : 0) | (Tuesday ? Weekdays.Tuesday : 0) | (Wednesday ? Weekdays.Wednesday : 0)
                   | (Thursday ? Weekdays.Thursday : 0) | (Friday ? Weekdays.Friday : 0) | (Saturday ? Weekdays.Saturday : 0) | (Sunday ? Weekdays.Sunday : 0);
        var weeks = (First ? WeekOfMonth.First : 0) | (Second ? WeekOfMonth.Second : 0) | (Third ? WeekOfMonth.Third : 0)
                    | (Fourth ? WeekOfMonth.Fourth : 0) | (Last ? WeekOfMonth.Last : 0);
        var spec = _original with
        {
            Kind = K,
            Enabled = Enabled,
            Start = HasStart ? start : _original.Kind == K ? _original.Start : null,
            End = Expires ? (EndDate ?? DateTime.Today) + (EndTime ?? TimeSpan.Zero) : null,
            Interval = (int)Math.Max(1, Interval ?? 1),
            Days = HasWeekdays ? days : Weekdays.None,
            Weeks = IsMonthlyWeekday ? weeks : WeekOfMonth.None,
            DaysOfMonth = IsMonthly ? ParseDays(DaysOfMonth) : 0,
            LastDayOfMonth = IsMonthly && LastDayOfMonth,
            Months = IsMonthly || IsMonthlyWeekday ? (_original.Months == MonthSet.None ? MonthSet.All : _original.Months) : MonthSet.None,
            RepeatEvery = CanRepeat && Repeat ? RepeatEvery.Span : null,
            RepeatFor = CanRepeat && Repeat && !RepeatForever ? RepeatFor.Span : null,
            Delay = CanDelay && HasDelay ? Delay.Span : null,
            RandomDelay = HasStart ? _original.RandomDelay : null,
            UserId = HasUser && !AnyUser && !string.IsNullOrWhiteSpace(UserId) ? UserId.Trim() : null,
            EventLog = IsEvent ? Blank(EventLog) : null,
            EventSource = IsEvent ? Blank(EventSource) : null,
            EventId = IsEvent && int.TryParse(EventId, out var id) ? id : null,
            SessionChange = IsSession ? SessionChange.Value : null,
        };
        return spec;
    }

    /// <summary>"1, 15, 28" → bits for those days; anything else in the box is ignored.</summary>
    internal static uint ParseDays(string text)
    {
        uint bits = 0;
        foreach (var part in text.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(part, out var day) && day is >= 1 and <= 31)
                bits |= 1u << (day - 1);
        return bits;
    }

    static string? Blank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
