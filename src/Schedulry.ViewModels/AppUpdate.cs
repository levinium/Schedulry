using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Schedulry.ViewModels;

/// <summary>
/// This copy's version, and a newer one downloaded in the background and ready to install. Set by
/// the app's update check; a development build is never "ready", so nothing offers a restart.
/// </summary>
public sealed partial class AppUpdate : ObservableObject
{
    public static AppUpdate Current { get; } = new();

    Action? _restart;
    Func<Task<string>>? _check;

    /// <summary>"Version 0.1.0", and how it stays up to date.</summary>
    [ObservableProperty] public partial string VersionText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady), nameof(ReadyText), nameof(RestartText))]
    [NotifyCanExecuteChangedFor(nameof(CheckNowCommand))]
    public partial string? ReadyVersion { get; private set; }

    /// <summary>An installed copy, which can look for a newer version when asked. Shows the button.</summary>
    public bool CanCheck => _check is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckNowCommand))]
    public partial bool IsChecking { get; private set; }

    /// <summary>What the last check by hand found: "You have the latest version", or why it couldn't look.</summary>
    [ObservableProperty] public partial string? CheckText { get; private set; }

    /// <summary>
    /// Turns on Check for Updates. <paramref name="check"/> looks, downloads a newer version (then
    /// calls <see cref="Ready"/>), and returns what it found in words.
    /// </summary>
    public void AllowChecks(Func<Task<string>> check)
    {
        _check = check;
        OnPropertyChanged(nameof(CanCheck));
        CheckNowCommand.NotifyCanExecuteChanged();
    }

    bool CanCheckNow => _check is not null && !IsChecking && !IsReady;

    [RelayCommand(CanExecute = nameof(CanCheckNow))]
    async Task CheckNow()
    {
        IsChecking = true;
        CheckText = "Looking for a newer version…";
        try
        {
            CheckText = await _check!();
        }
        catch (Exception e)
        {
            CheckText = $"Couldn't check for updates: {e.Message}";
        }
        finally
        {
            IsChecking = false;
        }
    }

    public bool IsReady => ReadyVersion is not null;
    public string ReadyText => $"Schedulry {ReadyVersion} is downloaded and ready. It installs when Schedulry restarts.";
    public string RestartText => $"Restart to Update to {ReadyVersion}";

    /// <summary>A new version has downloaded; <paramref name="restart"/> installs it and opens Schedulry again.</summary>
    public void Ready(string version, Action restart)
    {
        _restart = restart;
        ReadyVersion = version;
    }

    [RelayCommand]
    void Restart() => _restart?.Invoke();
}
