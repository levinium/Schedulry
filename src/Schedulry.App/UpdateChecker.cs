using Avalonia.Threading;
using Schedulry.ViewModels;
using Velopack;
using Velopack.Sources;

namespace Schedulry.App;

/// <summary>
/// Looks for a newer release when the app opens, every few hours, and when asked (Check
/// for Updates in Settings); downloads it in the background and offers a restart (<see cref="AppUpdate"/>).
/// Without one, a downloaded update installs the next time the app starts (Velopack does that on
/// its own), such as at sign-in. Only installed copies update: a development build does nothing.
/// </summary>
public static class UpdateChecker
{
    const string Repository = "https://github.com/levinium/Schedulry";

    /// <summary>The release lists, copied here by the Packages workflow (see <see cref="ReleaseFeedSource"/>).</summary>
    const string Feed = "https://raw.githubusercontent.com/levinium/Schedulry/updates/";

    static readonly TimeSpan Every = TimeSpan.FromHours(4);

    // One check at a time: the timed one and one asked for by hand never overlap.
    static readonly SemaphoreSlim Gate = new(1, 1);
    static UpdateManager? _manager;
    static Action _beforeRestart = () => { };
    static string? _downloaded;

    /// <summary><paramref name="beforeRestart"/> tidies up (the notification-area icon) before the update closes the app.</summary>
    public static void Start(Action beforeRestart)
    {
        var version = typeof(UpdateChecker).Assembly.GetName().Version;
        var number = version is null ? "" : $"{version.Major}.{version.Minor}.{version.Build}";
        try
        {
            _manager = new UpdateManager(new ReleaseFeedSource(Repository, Feed));
            if (!_manager.IsInstalled)
            {
                AppUpdate.Current.VersionText = $"Version {number}, a development build: it doesn't update itself.";
                return;
            }
        }
        catch (Exception)
        {
            return;
        }
        _beforeRestart = beforeRestart;
        AppUpdate.Current.VersionText = $"Version {_manager.CurrentVersion}. Updates download by themselves from GitHub.";
        AppUpdate.Current.AllowChecks(CheckOnceAsync);
        _ = LoopAsync();
    }

    static async Task LoopAsync()
    {
        while (_downloaded is null)
        {
            try
            {
                await CheckOnceAsync();
            }
            catch (Exception)
            {
                // Offline, rate-limited or GitHub unreachable: try again later; updates never get in the way.
            }
            if (_downloaded is not null)
                return;
            await Task.Delay(Every);
        }
    }

    /// <summary>Looks once, downloads what it finds, and says in words what happened. Throws when GitHub can't be reached.</summary>
    static async Task<string> CheckOnceAsync()
    {
        var manager = _manager!;
        await Gate.WaitAsync();
        try
        {
            if (_downloaded is not null)
                return $"Schedulry {_downloaded} is downloaded and ready.";
            if (await manager.CheckForUpdatesAsync() is not { } update)
                return $"You have the latest version ({manager.CurrentVersion}).";
            var target = update.TargetFullRelease;
            await manager.DownloadUpdatesAsync(update);
            _downloaded = target.Version.ToString();
            Dispatcher.UIThread.Post(() => AppUpdate.Current.Ready(target.Version.ToString(), () =>
            {
                _beforeRestart();
                manager.ApplyUpdatesAndRestart(target);
            }));
            return $"Schedulry {_downloaded} is downloaded and ready.";
        }
        finally
        {
            Gate.Release();
        }
    }
}
