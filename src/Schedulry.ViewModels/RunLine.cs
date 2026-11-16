using Schedulry.Core;

namespace Schedulry.ViewModels;

/// <summary>
/// One run in a list: the preview's timeline or Recent Activity. Built from the history log, or,
/// while the log is off, from what Schedulry saw happen since it opened.
/// </summary>
public sealed record RunLine
{
    public required string Path { get; init; }
    public string Name => Path[(Path.LastIndexOf('\\') + 1)..];
    public string Folder => Path.LastIndexOf('\\') <= 0 ? "\\" : Path[..Path.LastIndexOf('\\')];
    public required DateTime Time { get; init; }
    public required string WhenText { get; init; }
    public required string WhatText { get; init; }
    public required ChipKind Kind { get; init; }
    public string TookText { get; init; } = "";
    public string CauseText { get; init; } = "";

    public static RunLine From(RunRecord run, DateTime now) => new()
    {
        Path = run.Path,
        Time = run.Start,
        WhenText = When.At(run.Start, now),
        WhatText = run.OutcomeText,
        Kind = KindOf(run.Outcome),
        TookText = run.Outcome == RunEnd.Running ? $"{When.Took(now - run.Start)} so far"
            : run.Duration is { } took && run.Outcome is not (RunEnd.Skipped or RunEnd.FailedToStart) ? When.Took(took) : "",
        CauseText = run.CauseText,
    };

    public static ChipKind KindOf(RunEnd outcome) => outcome switch
    {
        RunEnd.Running => ChipKind.Info,
        RunEnd.Succeeded => ChipKind.Success,
        RunEnd.Failed or RunEnd.FailedToStart => ChipKind.Danger,
        RunEnd.Stopped or RunEnd.Skipped => ChipKind.Warning,
        _ => ChipKind.Neutral,
    };

    /// <summary>A start or finish Schedulry saw itself (the history log being off).</summary>
    public static RunLine Seen(TaskSnapshot task, bool started, DateTime now)
    {
        if (started)
            return new RunLine { Path = task.Path, Time = now, WhenText = When.At(now, now), WhatText = "Started", Kind = ChipKind.Info };
        var result = task.LastResult is { } code ? ResultCodes.Describe(code) : null;
        return new RunLine
        {
            Path = task.Path,
            Time = now,
            WhenText = When.At(now, now),
            WhatText = result is null ? "Finished" : $"Finished · {result.Short}",
            Kind = result?.Outcome switch
            {
                TaskOutcome.Succeeded => ChipKind.Success,
                TaskOutcome.Failed => ChipKind.Danger,
                TaskOutcome.Warning => ChipKind.Warning,
                _ => ChipKind.Neutral,
            },
            TookText = task.LastRun is { } start && now > start ? When.Took(now - start) : "",
        };
    }
}
