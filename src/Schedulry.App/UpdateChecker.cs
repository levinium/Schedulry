using Avalonia.Threading;
using Schedulry.ViewModels;
using Velopack;
using Velopack.Sources;

namespace Schedulry.App;

/// <summary>
/// Looks for a newer release on GitHub when the app opens and every few hours, downloads it in the
/// background, and offers a restart (<see cref="AppUpdate"/>). Without one, a downloaded update
/// installs the next time the app starts (Velopack does that on its own), such as at sign-in.
/// Only installed copies update: a development build does nothing. Releases are pre-releases
/// while the builds are unsigned, so pre-releases are included.
/// </summary>
public static class UpdateChecker
{
    const string Repository = "https://github.com/levinium/Schedulry";

    static readonly TimeSpan Every = TimeSpan.FromHours(4);

    /// <summary><paramref name="beforeRestart"/> tidies up (the notification-area icon) before the update closes the app.</summary>
    public static void Start(Action beforeRestart)
    {
        var version = typeof(UpdateChecker).Assembly.GetName().Version;
        var number = version is null ? "" : $"{version.Major}.{version.Minor}.{version.Build}";
        UpdateManager manager;
        try
        {
            manager = new UpdateManager(new GithubSource(Repository, null, true));
            if (!manager.IsInstalled)
            {
                AppUpdate.Current.VersionText = $"Version {number}, a development build: it doesn't update itself.";
                return;
            }
        }
        catch (Exception)
        {
            return;
        }
        AppUpdate.Current.VersionText = $"Version {manager.CurrentVersion}. Updates download by themselves from GitHub.";
        _ = LoopAsync(manager, beforeRestart);
    }

    static async Task LoopAsync(UpdateManager manager, Action beforeRestart)
    {
        while (true)
        {
            try
            {
                if (await manager.CheckForUpdatesAsync() is { } update)
                {
                    await manager.DownloadUpdatesAsync(update);
                    var target = update.TargetFullRelease;
                    Dispatcher.UIThread.Post(() => AppUpdate.Current.Ready(target.Version.ToString(), () =>
                    {
                        beforeRestart();
                        manager.ApplyUpdatesAndRestart(target);
                    }));
                    return;
                }
            }
            catch (Exception)
            {
                // Offline, rate-limited or GitHub unreachable: try again later; updates never get in the way.
            }
            await Task.Delay(Every);
        }
    }
}
