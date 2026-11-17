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

    /// <summary>"Version 0.1.0", and how it stays up to date.</summary>
    [ObservableProperty] public partial string VersionText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady), nameof(ReadyText), nameof(RestartText))]
    public partial string? ReadyVersion { get; private set; }

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
