namespace Schedulry.ViewModels;

/// <summary>
/// What the view models need from the app around them. The app fills these in at start-up; tests
/// and the snapshot tool leave the defaults, which run work inline.
/// </summary>
public static class Platform
{
    /// <summary>Runs an action on the UI thread.</summary>
    public static Action<Action> Post { get; set; } = action => action();

    /// <summary>Runs slow work (COM calls) off the UI thread.</summary>
    public static Func<Func<Task>, Task> Background { get; set; } = work => Task.Run(work);

    /// <summary>Starts this app again as administrator (a UAC prompt) and closes this copy. False if the person said no.</summary>
    public static Func<bool> RestartAsAdmin { get; set; } = () => false;

    /// <summary>Turns on Task Scheduler's history log (a UAC prompt when not elevated). False if it didn't happen.</summary>
    public static Func<Task<bool>> EnableHistory { get; set; } = () => Task.FromResult(false);

    public static Action<string> CopyText { get; set; } = _ => { };

    /// <summary>Asks where to save a text file (suggested name, contents). False if the person cancelled.</summary>
    public static Func<string, string, Task<bool>> SaveTextFile { get; set; } = (_, _) => Task.FromResult(false);
}

/// <summary>Colour family of a status chip; each maps to a Badge*Bg/Fg/Dot token set.</summary>
public enum ChipKind
{
    Neutral,
    Info,
    Success,
    Warning,
    Danger,
}
