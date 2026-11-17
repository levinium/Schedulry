using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
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
            Platform.Connect = demo ? request => DemoSchedulerHost.Connect(request) : WindowsSchedulerHost.Connect;
            IComputerStore computers = demo
                ? new MemoryComputerStore([new SavedComputer("FILESRV01"), new SavedComputer("BUILD-02", @"CONTOSO\ops.admin")])
                : new JsonComputerStore(Path.Combine(Program.DataFolder, "computers.json"));
            IPasswordVault passwords = demo ? new MemoryPasswordVault() : new CredentialVault();
            Views.MainView.ColumnLayoutFile = Path.Combine(Program.DataFolder, "columns.json");
            var window = new MainWindow();
            MainViewModel? main = null;

            // One view model per computer. Switching builds the next one, then retires the last.
            void Show(ISchedulerHost host)
            {
                // Stars and groups: one file per computer.
                IOrganizerStore organizer = demo
                    ? new MemoryOrganizerStore(host.IsRemote ? null : DemoOrganizer.Sample())
                    : new JsonOrganizerStore(Path.Combine(Program.DataFolder, $"organize-{host.ComputerName.ToLowerInvariant()}.json"));
                var next = new MainViewModel(host, organizer: organizer, computers: computers, passwords: passwords);
                next.SwitchRequested += Show;
                var previous = main;
                main = next;
                window.DataContext = next;
                if (previous is not null)
                {
                    previous.SwitchRequested -= Show;
                    // Its COM connections close off the UI thread; a remote one can take a moment.
                    _ = Task.Run(previous.Dispose);
                    next.Start();
                }
            }
            Show(demo ? new DemoSchedulerHost() : new WindowsSchedulerHost());

            Platform.RestartAsAdmin = () =>
            {
                if (!Elevation.StartElevatedCopy(Program.Args))
                    return false;
                desktop.Shutdown();
                return true;
            };
            Platform.EnableHistory = Elevation.EnableHistoryAsync;
            Platform.CopyText = text => _ = window.Clipboard?.SetTextAsync(text);
            Platform.PickFile = async title =>
            {
                var files = await window.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions { Title = title, AllowMultiple = false });
                return files.Count == 0 ? null : files[0].TryGetLocalPath();
            };
            Platform.OpenTextFile = async (title, typeName, pattern) =>
            {
                var files = await window.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
                {
                    Title = title,
                    AllowMultiple = false,
                    FileTypeFilter = [new Avalonia.Platform.Storage.FilePickerFileType(typeName) { Patterns = [pattern] }],
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
                var file = await window.StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
                {
                    Title = "Save a Copy of the Task",
                    SuggestedFileName = name,
                    DefaultExtension = "xml",
                    FileTypeChoices = [new Avalonia.Platform.Storage.FilePickerFileType("Task Scheduler XML") { Patterns = ["*.xml"] }],
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
            desktop.MainWindow = window;
            desktop.ShutdownRequested += (_, _) => main?.Dispose();
            window.Opened += (_, _) => main?.Start();
            var clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => main?.RefreshClock());
            clock.Start();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
