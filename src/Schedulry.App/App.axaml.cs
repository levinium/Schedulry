using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Schedulry.Core;
using Schedulry.Demo;
using Schedulry.ViewModels;
using Schedulry.Windows;

namespace Schedulry.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Platform.Post = action => Dispatcher.UIThread.Post(action);
            var demo = Program.Demo;
            Platform.Connect = demo ? request => DemoSchedulerHost.Connect(request) : WindowsSchedulerHost.Connect;
            IComputerStore computers = demo
                ? new MemoryComputerStore([new SavedComputer("FILESRV01"), new SavedComputer("BUILD-02", @"CONTOSO\ops.admin")])
                : new JsonComputerStore(Path.Combine(Program.DataFolder, "computers.json"));
            IPasswordVault passwords = demo ? new MemoryPasswordVault() : new CredentialVault();
            var settings = demo
                ? new SettingsViewModel(new MemorySettingsStore())
                : new SettingsViewModel(new JsonSettingsStore(Path.Combine(Program.DataFolder, "settings.json")), new StartupRegistration());
            Views.MainView.ColumnLayoutFile = Path.Combine(Program.DataFolder, "columns.json");
            var window = new MainWindow();
            MainViewModel? main = null;
            var exiting = false;
            var tray = BuildTray(settings, OpenWindow, Exit);
            UpdateChecker.Start(() =>
            {
                exiting = true;
                tray.IsVisible = false;
                main?.Dispose();
            });

            // Stars, groups and muted alerts: one file per computer.
            string OrganizerFile(string computer) => Path.Combine(Program.DataFolder, $"organize-{computer.ToLowerInvariant()}.json");

            // One view model per computer. Switching builds the next one, then retires the last.
            void Show(ISchedulerHost host)
            {
                IOrganizerStore organizer = demo
                    ? new MemoryOrganizerStore(host.IsRemote ? null : DemoOrganizer.Sample())
                    : new JsonOrganizerStore(OrganizerFile(host.ComputerName));
                var next = new MainViewModel(host, organizer: organizer, computers: computers, passwords: passwords, settings: settings);
                next.SwitchRequested += Show;
                next.RunFailed += Notifier.Show;
                next.InBackground = !window.IsVisible;
                var previous = main;
                main = next;
                window.DataContext = next;
                tray.ToolTipText = $"Schedulry · {host.ComputerName}";
                if (previous is not null)
                {
                    previous.SwitchRequested -= Show;
                    previous.RunFailed -= Notifier.Show;
                    // Its COM connections close off the UI thread; a remote one can take a moment.
                    _ = Task.Run(previous.Dispose);
                    next.Start();
                }
            }

            void OpenWindow()
            {
                window.Show();
                if (window.WindowState == WindowState.Minimized)
                    window.WindowState = WindowState.Normal;
                window.Activate();
                if (main is not null)
                    main.InBackground = false;
            }

            void HideWindow()
            {
                window.Hide();
                if (main is not null)
                    main.InBackground = true;
                if (settings.TakeTrayNote())
                    Notifier.ShowNote("Schedulry is still running",
                        "It keeps watching for failed runs from the notification area. Exit it there, or change this in Settings.");
            }

            void Exit()
            {
                exiting = true;
                desktop.Shutdown();
            }

            // Closing the window keeps the app watching from the notification area, unless that's turned off.
            window.Closing += (_, e) =>
            {
                if (exiting || e.CloseReason != WindowCloseReason.WindowClosing || !settings.KeepRunningWhenClosed)
                    return;
                e.Cancel = true;
                HideWindow();
            };
            window.Closed += (_, _) =>
            {
                if (!exiting)
                    Exit();
            };

            // A click on a notification: from this copy's own, or Windows started this copy for it.
            Notifier.Activated += (action, computer, path) => Dispatcher.UIThread.Post(() =>
            {
                var here = main is not null && computer is not null && ComputerNames.Same(computer, main.ComputerName);
                if (action == NoticeAction.Mute && computer is not null && path is not null)
                {
                    if (here)
                        main!.SetMuted(path, true);
                    else if (!demo)
                    {
                        var store = new JsonOrganizerStore(OrganizerFile(computer));
                        var data = store.Load();
                        data.SetMuted(path, true);
                        store.Save(data);
                    }
                    return;
                }
                OpenWindow();
                if (here && path is not null)
                    main!.ShowTask(path);
            });
            Notifier.Listen();
            SingleInstance.OnShowRequested(() => Dispatcher.UIThread.Post(OpenWindow));

            Show(demo ? new DemoSchedulerHost() : new WindowsSchedulerHost());

            Platform.RestartAsAdmin = () =>
            {
                if (!Elevation.StartElevatedCopy([.. Program.Args, Program.RestartedFlag]))
                    return false;
                Exit();
                return true;
            };
            Platform.EnableHistory = Elevation.EnableHistoryAsync;
            Platform.CopyText = text => _ = window.Clipboard?.SetTextAsync(text);
            Platform.PickFile = async title =>
            {
                var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false });
                return files.Count == 0 ? null : files[0].TryGetLocalPath();
            };
            Platform.OpenTextFile = async (title, typeName, pattern) =>
            {
                var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = title,
                    AllowMultiple = false,
                    FileTypeFilter = [new FilePickerFileType(typeName) { Patterns = [pattern] }],
                });
                if (files.Count == 0)
                    return null;
                await using var stream = await files[0].OpenReadAsync();
                // Task Scheduler writes UTF-16 with a byte-order mark; the reader follows whatever mark there is.
                using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                return (files[0].Name, await reader.ReadToEndAsync());
            };
            Platform.SaveTextFile = async (name, text) =>
            {
                var file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Save a Copy of the Task",
                    SuggestedFileName = name,
                    DefaultExtension = "xml",
                    FileTypeChoices = [new FilePickerFileType("Task Scheduler XML") { Patterns = ["*.xml"] }],
                });
                if (file is null)
                    return false;
                await using var stream = await file.OpenWriteAsync();
                // UTF-16 with a byte-order mark, as Task Scheduler itself exports, so its Import accepts it.
                await using var writer = new StreamWriter(stream, new System.Text.UnicodeEncoding(bigEndian: false, byteOrderMark: true));
                await writer.WriteAsync(text);
                return true;
            };

            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                Program.WriteCrashLog(e.Exception);
                e.Handled = true;
            };
            desktop.ShutdownRequested += (_, _) =>
            {
                tray.IsVisible = false;   // otherwise the icon lingers until the pointer passes over it
                main?.Dispose();
            };
            window.Opened += (_, _) => main?.Start();

            // Started with Windows, or for a notification click: watch from the notification area.
            if (Program.StartInTray || Notifier.StartedByClick)
                main?.Start();
            else
                window.Show();

            // Once a second: the "running for" timers, and a slower pace while nobody is looking.
            var clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
            {
                if (main is null)
                    return;
                var away = !window.IsVisible || window.WindowState == WindowState.Minimized;
                main.InBackground = away;
                if (!away)
                    main.RefreshClock();
            });
            clock.Start();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>The notification-area icon: open, the two switches people reach for there, and exit.</summary>
    TrayIcon BuildTray(SettingsViewModel settings, Action open, Action exit)
    {
        var openItem = new NativeMenuItem("Open Schedulry");
        openItem.Click += (_, _) => open();
        var exitItem = new NativeMenuItem("Exit");
        exitItem.Click += (_, _) => exit();
        // Shown once a newer version has downloaded.
        var update = AppUpdate.Current;
        var updateItem = new NativeMenuItem { IsVisible = false };
        updateItem.Click += (_, _) => update.RestartCommand.Execute(null);
        update.PropertyChanged += (_, _) =>
        {
            updateItem.Header = update.RestartText;
            updateItem.IsVisible = update.IsReady;
        };
        var alerts = new NativeMenuItem("Failure Alerts") { ToggleType = MenuItemToggleType.CheckBox, IsChecked = settings.NotifyFailures };
        alerts.Click += (_, _) => settings.NotifyFailures = !settings.NotifyFailures;
        var startup = new NativeMenuItem("Start with Windows") { ToggleType = MenuItemToggleType.CheckBox, IsChecked = settings.StartWithWindows };
        startup.Click += (_, _) => settings.StartWithWindows = !settings.StartWithWindows;
        settings.PropertyChanged += (_, _) =>
        {
            alerts.IsChecked = settings.NotifyFailures;
            startup.IsChecked = settings.StartWithWindows;
        };

        var tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Schedulry/Assets/schedulry.ico"))),
            ToolTipText = "Schedulry",
            Menu =
            [
                openItem,
                updateItem,
                new NativeMenuItemSeparator(),
                alerts,
                startup,
                new NativeMenuItemSeparator(),
                exitItem,
            ],
        };
        tray.Clicked += (_, _) => open();
        TrayIcon.SetIcons(this, [tray]);
        return tray;
    }
}
