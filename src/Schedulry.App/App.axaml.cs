using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
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
            var demo = Program.Args.Contains("--demo", StringComparer.OrdinalIgnoreCase);
            ISchedulerHost host = demo ? new DemoSchedulerHost() : new WindowsSchedulerHost();
            // Stars and groups: one file per computer, so a remote computer later gets its own.
            IOrganizerStore organizer = demo
                ? new MemoryOrganizerStore(DemoOrganizer.Sample())
                : new JsonOrganizerStore(Path.Combine(Program.DataFolder, $"organize-{host.ComputerName.ToLowerInvariant()}.json"));
            var main = new MainViewModel(host, organizer: organizer);
            Views.MainView.ColumnLayoutFile = Path.Combine(Program.DataFolder, "columns.json");
            var window = new MainWindow { DataContext = main };

            Platform.RestartAsAdmin = () =>
            {
                if (!Elevation.StartElevatedCopy(Program.Args))
                    return false;
                desktop.Shutdown();
                return true;
            };
            Platform.EnableHistory = Elevation.EnableHistoryAsync;
            Platform.CopyText = text => _ = window.Clipboard?.SetTextAsync(text);

            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                Program.WriteCrashLog(e.Exception);
                e.Handled = true;
            };
            desktop.MainWindow = window;
            desktop.ShutdownRequested += (_, _) => main.Dispose();
            window.Opened += (_, _) => main.Start();
            var clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => main.RefreshClock());
            clock.Start();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
