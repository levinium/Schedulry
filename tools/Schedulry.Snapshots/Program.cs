// Renders Schedulry's screens from the demo computer at a fixed moment, light and dark.
//   schedulry-snapshots <output folder>
// Everything stays on the UI thread: the monitor is stepped by hand, background work runs inline,
// and frames are forced, so a run can't hang on a headless await.
using Avalonia;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using Schedulry.App;
using Schedulry.Demo;
using Schedulry.ViewModels;

var output = Path.GetFullPath(args.Length > 0 ? args[0] : "snapshots-out");
Directory.CreateDirectory(output);

AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .WithInterFont()
    .SetupWithoutStarting();

Platform.Post = action => action();
Platform.Background = work => work();

// 10:00:00 today: the printer watchdog is mid-run and the payroll sync has just failed.
var moment = DateTime.Today.AddHours(10);

foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
{
    var suffix = theme == ThemeVariant.Light ? "light" : "dark";
    Application.Current!.RequestedThemeVariant = theme;

    Render($"main-{suffix}", new DemoSchedulerHost(() => moment), vm =>
        vm.SelectedRow = vm.Rows.First(r => r.Name == "Sync payroll exports"));

    Render($"running-{suffix}", new DemoSchedulerHost(() => moment), vm =>
    {
        vm.SelectedView = vm.Views.First(v => v.Key == "running");
        vm.SelectedRow = vm.Rows.FirstOrDefault();
    });

    Render($"windows-tasks-{suffix}", new DemoSchedulerHost(() => moment, elevated: true, historyEnabled: true), vm =>
    {
        vm.ShowWindowsTasks = true;
        vm.SelectedSort = vm.SortOptions.First(s => s.Field == SortField.Status);
        vm.SelectedRow = vm.Rows.First(r => r.Name == "Mirror data to NAS");
    });
}

Console.WriteLine($"Wrote snapshots to {output}");
return 0;

void Render(string name, DemoSchedulerHost host, Action<MainViewModel> arrange)
{
    using var vm = new MainViewModel(host, clock: () => moment);
    vm.Monitor.Step();   // the first full read, raised inline
    arrange(vm);
    var window = new MainWindow { Width = 1440, Height = 900, DataContext = vm };
    window.Show();
    Settle();
    using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException($"No frame rendered for {name}");
#pragma warning disable CS0618 // the encoder-options overload adds nothing for a plain PNG
    frame.Save(Path.Combine(output, name + ".png"));
#pragma warning restore CS0618
    window.Close();
    Console.WriteLine($"  {name}.png");
}

static void Settle()
{
    for (var i = 0; i < 10; i++)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }
}
