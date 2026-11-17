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
    readonly IRemoteAccess _remote;
    bool _loading = true;

    public SettingsViewModel(ISettingsStore store, IStartup? startup = null, IRemoteAccess? remote = null)
    {
        _store = store;
        _startup = startup ?? new NoStartup();
        _remote = remote ?? new NoRemoteAccess();
        _settings = store.Load();
        NotifyFailures = _settings.NotifyFailures;
        NotifyWindowsTasks = _settings.NotifyWindowsTasks;
        KeepRunningWhenClosed = _settings.KeepRunningWhenClosed;
        AlwaysRunAsAdmin = _settings.AlwaysRunAsAdmin;
        StartWithWindows = _startup.IsOn;
        AllowRemote = _remote.IsOn;
        AllowLocalAdmins = _remote.LocalAdminsOn;
        _loading = false;
    }

    [ObservableProperty] public partial bool NotifyFailures { get; set; }
    [ObservableProperty] public partial bool NotifyWindowsTasks { get; set; }
    [ObservableProperty] public partial bool KeepRunningWhenClosed { get; set; }
    [ObservableProperty] public partial bool StartWithWindows { get; set; }

    /// <summary>Opening Schedulry restarts it as administrator; with Start with Windows, it starts that way at sign-in.</summary>
    [ObservableProperty] public partial bool AlwaysRunAsAdmin { get; set; }

    /// <summary>Other computers may manage this PC's tasks (the firewall lets them in).</summary>
    [ObservableProperty] public partial bool AllowRemote { get; set; }

    /// <summary>This PC's own administrator accounts keep their rights when signing in from another computer.</summary>
    [ObservableProperty] public partial bool AllowLocalAdmins { get; set; }

    [ObservableProperty] public partial string? Problem { get; private set; }

    public bool HasProblem => Problem is not null;
    partial void OnProblemChanged(string? value) => OnPropertyChanged(nameof(HasProblem));

    /// <summary>A change is waiting on Windows (often its administrator prompt): the switches that need one wait too.</summary>
    [ObservableProperty] public partial bool Busy { get; private set; }

    public bool CanChange => !Busy;
    partial void OnBusyChanged(bool value) => OnPropertyChanged(nameof(CanChange));

    /// <summary>The change in progress, if any (finished otherwise). Tests wait on it.</summary>
    public Task PendingChange { get; private set; } = Task.CompletedTask;

    /// <summary>Reads again what can change outside the app; called when the Settings card opens.</summary>
    public void Refresh()
    {
        if (Busy)
            return;
        ShowWhatWindowsHas();
    }

    void ShowWhatWindowsHas()
    {
        _loading = true;
        AllowRemote = _remote.IsOn;
        AllowLocalAdmins = _remote.LocalAdminsOn;
        StartWithWindows = _startup.IsOn;
        _loading = false;
    }

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

    partial void OnAlwaysRunAsAdminChanged(bool value)
    {
        Save();
        // Starting with Windows changes how it starts: the sign-in task, or the Run key.
        if (!_loading && StartWithWindows)
            PendingChange = ChangeAsync(() => _startup.SetAsync(true, value), "starting with Windows");
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_loading)
            return;
        PendingChange = ChangeAsync(() => _startup.SetAsync(value, AlwaysRunAsAdmin), "starting with Windows");
    }

    partial void OnAllowRemoteChanged(bool value)
    {
        if (_loading)
            return;
        PendingChange = ChangeAsync(() => _remote.SetAsync(value), "whether other computers can manage this one");
    }

    partial void OnAllowLocalAdminsChanged(bool value)
    {
        if (_loading)
            return;
        PendingChange = ChangeAsync(() => _remote.SetLocalAdminsAsync(value), "whether this PC's own administrator accounts can connect");
    }

    async Task ChangeAsync(Func<Task> change, string what)
    {
        Busy = true;
        try
        {
            // A No at the administrator prompt changes nothing and needs no explaining.
            await change();
            Problem = null;
        }
        catch (Exception e)
        {
            Problem = $"Couldn't change {what}: {e.Message}";
        }
        finally
        {
            // The switches show what Windows really has.
            ShowWhatWindowsHas();
            Busy = false;
        }
    }

    sealed class NoStartup : IStartup
    {
        public bool IsOn { get; private set; }

        public Task SetAsync(bool on, bool asAdmin)
        {
            IsOn = on;
            return Task.CompletedTask;
        }
    }

    sealed class NoRemoteAccess : IRemoteAccess
    {
        public bool IsOn { get; private set; }
        public bool LocalAdminsOn { get; private set; }

        public Task<bool> SetAsync(bool on)
        {
            IsOn = on;
            return Task.FromResult(true);
        }

        public Task<bool> SetLocalAdminsAsync(bool on)
        {
            LocalAdminsOn = on;
            return Task.FromResult(true);
        }
    }

    void Save()
    {
        if (_loading)
            return;
        _settings.NotifyFailures = NotifyFailures;
        _settings.NotifyWindowsTasks = NotifyWindowsTasks;
        _settings.KeepRunningWhenClosed = KeepRunningWhenClosed;
        _settings.AlwaysRunAsAdmin = AlwaysRunAsAdmin;
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
