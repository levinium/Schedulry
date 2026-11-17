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

    /// <summary>
    /// Opens a computer's Task Scheduler and checks it answers (slow: called off the UI thread).
    /// Throws with a plain-words reason when it can't.
    /// </summary>
    public static Func<Schedulry.Core.ConnectRequest, Schedulry.Core.ISchedulerHost> Connect { get; set; } =
        _ => throw new InvalidOperationException("Connecting to other computers isn't available here.");

    public static Action<string> CopyText { get; set; } = _ => { };

    /// <summary>Asks for a file to use (title); its full path, or null if the person cancelled.</summary>
    public static Func<string, Task<string?>> PickFile { get; set; } = _ => Task.FromResult<string?>(null);

    /// <summary>Asks for a file to read (title, file type, pattern); its name and text, or null.</summary>
    public static Func<string, string, string, Task<(string Name, string Text)?>> OpenTextFile { get; set; } =
        (_, _, _) => Task.FromResult<(string, string)?>(null);

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
