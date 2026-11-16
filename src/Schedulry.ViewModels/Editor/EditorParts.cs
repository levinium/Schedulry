using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Schedulry.Core;

namespace Schedulry.ViewModels.Editor;

/// <summary>A choice in a drop-down: the value and the words shown for it.</summary>
public sealed record Choice<T>(T Value, string Title)
{
    public override string ToString() => Title;
}

public enum DurationUnit
{
    Minutes,
    Hours,
    Days,
}

/// <summary>
/// A length of time as a number and a unit ("5 minutes", "2 hours"). Base for the editor's parts:
/// any change in a part tells the editor, which rebuilds its review.
/// </summary>
public abstract class EditorPart : ObservableObject
{
    Action? _changed;

    public void Attach(Action changed) => _changed = changed;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is not ("Summary" or "Title"))
            _changed?.Invoke();
    }

    protected void Changed() => _changed?.Invoke();
}

public sealed partial class DurationEditor : EditorPart
{
    public static IReadOnlyList<Choice<DurationUnit>> Units { get; } =
        [new(DurationUnit.Minutes, "minutes"), new(DurationUnit.Hours, "hours"), new(DurationUnit.Days, "days")];

    public DurationEditor(TimeSpan span)
    {
        // The largest unit that says it exactly: 90 minutes stays minutes, 2 hours is hours.
        if (span.TotalDays >= 1 && span.TotalDays == Math.Floor(span.TotalDays))
            (Amount, Unit) = ((decimal)span.TotalDays, Units[2]);
        else if (span.TotalHours >= 1 && span.TotalHours == Math.Floor(span.TotalHours))
            (Amount, Unit) = ((decimal)span.TotalHours, Units[1]);
        else
            (Amount, Unit) = ((decimal)Math.Max(1, Math.Round(span.TotalMinutes)), Units[0]);
    }

    [ObservableProperty] public partial decimal? Amount { get; set; }
    [ObservableProperty] public partial Choice<DurationUnit> Unit { get; set; }

    public TimeSpan Span
    {
        get
        {
            var amount = (double)Math.Max(0, Amount ?? 0);
            return Unit.Value switch
            {
                DurationUnit.Days => TimeSpan.FromDays(amount),
                DurationUnit.Hours => TimeSpan.FromHours(amount),
                _ => TimeSpan.FromMinutes(amount),
            };
        }
    }
}

/// <summary>One program the task runs, or another kind of action kept exactly as it was.</summary>
public sealed partial class ActionEditor : EditorPart
{
    readonly ActionInfo? _original;
    readonly Action<ActionEditor> _remove;

    public ActionEditor(DraftAction? action, Action<ActionEditor> remove)
    {
        _remove = remove;
        _original = action?.Action;
        Source = action?.Source;
        Program = action?.Action.Kind == ActionKind.Program ? action.Action.Path : "";
        Arguments = action?.Action.Arguments ?? "";
        StartIn = action?.Action.WorkingDirectory ?? "";
    }

    public int? Source { get; }
    public bool IsProgram => _original is null || _original.Kind == ActionKind.Program;
    public bool IsKept => !IsProgram;
    public string KeptSummary => _original?.Summary ?? "";
    public int Number { get; set; }

    [ObservableProperty] public partial string Program { get; set; }
    [ObservableProperty] public partial string Arguments { get; set; }
    [ObservableProperty] public partial string StartIn { get; set; }

    [RelayCommand]
    void Remove() => _remove(this);

    [RelayCommand]
    async Task BrowseAsync()
    {
        if (await Platform.PickFile("Choose the Program to Run") is { } path)
            Program = path;
    }

    public DraftAction ToDraft() => IsKept
        ? new DraftAction(_original!, Source)
        : new DraftAction(new ActionInfo(ActionKind.Program, Program.Trim().Trim('"'), Blank(Arguments), Blank(StartIn)?.Trim('"')), Source);

    static string? Blank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
