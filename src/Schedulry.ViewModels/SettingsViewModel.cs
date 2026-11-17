using CommunityToolkit.Mvvm.ComponentModel;
using Schedulry.Core;

namespace Schedulry.ViewModels;

/// <summary>
/// The app's settings: one for the whole app, shared by the window (whichever computer it shows)
/// and the notification-area menu. Each change is saved straight away.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    readonly ISettingsStore _store;
    readonly AppSettings _settings;
    readonly IStartup _startup;
    bool _loading = true;

    public SettingsViewModel(ISettingsStore store, IStartup? startup = null)
    {
        _store = store;
        _startup = startup ?? new NoStartup();
        _settings = store.Load();
        NotifyFailures = _settings.NotifyFailures;
        NotifyWindowsTasks = _settings.NotifyWindowsTasks;
        KeepRunningWhenClosed = _settings.KeepRunningWhenClosed;
        StartWithWindows = _startup.IsOn;
        _loading = false;
    }

    [ObservableProperty] public partial bool NotifyFailures { get; set; }
    [ObservableProperty] public partial bool NotifyWindowsTasks { get; set; }
    [ObservableProperty] public partial bool KeepRunningWhenClosed { get; set; }
    [ObservableProperty] public partial bool StartWithWindows { get; set; }
    [ObservableProperty] public partial string? Problem { get; private set; }

    public bool HasProblem => Problem is not null;
    partial void OnProblemChanged(string? value) => OnPropertyChanged(nameof(HasProblem));

    /// <summary>True the first time only: the window has gone to the notification area and the person should know.</summary>
    public bool TakeTrayNote()
    {
        if (_settings.TrayNoteShown)
            return false;
        _settings.TrayNoteShown = true;
        Save();
        return true;
    }

    /// <summary>Whether a failure of this task should send a notification (before the per-task setting).</summary>
    public bool Wants(TaskSnapshot task) => NotifyFailures && (NotifyWindowsTasks || !task.IsWindowsTask);

    partial void OnNotifyFailuresChanged(bool value) => Save();
    partial void OnNotifyWindowsTasksChanged(bool value) => Save();
    partial void OnKeepRunningWhenClosedChanged(bool value) => Save();

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_loading)
            return;
        try
        {
            _startup.Set(value);
            Problem = null;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Problem = $"Couldn't change starting with Windows: {e.Message}";
            // The switch shows what is really set.
            _loading = true;
            StartWithWindows = _startup.IsOn;
            _loading = false;
        }
    }

    sealed class NoStartup : IStartup
    {
        public bool IsOn { get; private set; }
        public void Set(bool on) => IsOn = on;
    }

    void Save()
    {
        if (_loading)
            return;
        _settings.NotifyFailures = NotifyFailures;
        _settings.NotifyWindowsTasks = NotifyWindowsTasks;
        _settings.KeepRunningWhenClosed = KeepRunningWhenClosed;
        try
        {
            _store.Save(_settings);
            Problem = null;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            Problem = $"Couldn't save the settings: {e.Message}";
        }
    }
}
