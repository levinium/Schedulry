using System.Runtime.InteropServices;

namespace Schedulry.App;

/// <summary>
/// One copy per person signed in. Starting it again brings the running copy's window forward
/// instead of opening a second one (which would watch, and notify, twice).
/// </summary>
internal static class SingleInstance
{
    static Mutex? _mutex;
    static EventWaitHandle? _show;

    /// <summary>
    /// True when this copy should run. False when another is running: it has been asked to show
    /// its window, unless <paramref name="quiet"/> (started with Windows, nothing to show).
    /// <paramref name="waitForPrevious"/> is for a restart as administrator, while the old copy closes.
    /// </summary>
    public static bool Claim(string name, bool quiet, bool waitForPrevious)
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: true, $@"Local\{name}", out var first);
            if (!first && !Wait(_mutex, waitForPrevious ? TimeSpan.FromSeconds(20) : TimeSpan.Zero))
            {
                if (!quiet && EventWaitHandle.TryOpenExisting($@"Local\{name}.Show", out var show))
                {
                    // The running copy may bring its window to the front: this one has the right to, and passes it on.
                    _ = AllowSetForegroundWindow(AnyProcess);
                    show.Set();
                    show.Dispose();
                }
                _mutex.Dispose();
                _mutex = null;
                return false;
            }
        }
        catch (UnauthorizedAccessException)
        {
            // The running copy is elevated and this one can't reach it: run alongside rather than do nothing.
            _mutex = null;
        }
        _show = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{name}.Show", out _);
        return true;
    }

    /// <summary>Calls <paramref name="show"/> (on a background thread) whenever another copy asks.</summary>
    public static void OnShowRequested(Action show)
    {
        if (_show is not { } handle)
            return;
        new Thread(() =>
        {
            while (handle.WaitOne())
                show();
        }) { IsBackground = true, Name = "Schedulry show requests" }.Start();
    }

    static bool Wait(Mutex mutex, TimeSpan timeout)
    {
        try
        {
            return mutex.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            return true;   // the previous copy ended without letting go: it's ours now
        }
    }

    const int AnyProcess = -1;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool AllowSetForegroundWindow(int processId);
}
