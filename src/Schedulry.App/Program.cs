using Avalonia;

namespace Schedulry.App;

internal static class Program
{
    /// <summary>The arguments the app started with, passed on when it restarts as administrator.</summary>
    internal static string[] Args { get; private set; } = [];

    /// <summary>Started with Windows: the window stays in the notification area until it's opened.</summary>
    internal static bool StartInTray { get; private set; }

    internal static bool Demo => Args.Contains("--demo", StringComparer.OrdinalIgnoreCase);

    // Switches for this start only, never passed on.
    const string TrayFlag = "--tray";
    internal const string RestartedFlag = "--restarted";

    [STAThread]
    public static void Main(string[] args)
    {
        bool Has(string flag) => args.Contains(flag, StringComparer.OrdinalIgnoreCase);
        StartInTray = Has(TrayFlag);
        // Windows adds -ToastActivated when it starts the app for a notification click.
        Args = args.Where(a => !a.Equals(TrayFlag, StringComparison.OrdinalIgnoreCase)
            && !a.Equals(RestartedFlag, StringComparison.OrdinalIgnoreCase)
            && !a.Equals("-ToastActivated", StringComparison.OrdinalIgnoreCase)).ToArray();
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrashLog(e.ExceptionObject);

        // The demo computer runs beside the real one, so it has its own name.
        if (!SingleInstance.Claim(Demo ? "Schedulry.Demo" : "Schedulry", quiet: StartInTray, waitForPrevious: Has(RestartedFlag)))
            return;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
    }

    // Named exactly this because Avalonia's XAML previewer looks it up by convention.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    internal static string DataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Schedulry");

    /// <summary>Keeps a crash's stack trace in logs/crash-*.txt under the app-data folder.</summary>
    internal static void WriteCrashLog(object exception)
    {
        try
        {
            var folder = Path.Combine(DataFolder, "logs");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.txt"),
                $"Schedulry {typeof(Program).Assembly.GetName().Version} on {Environment.OSVersion}{Environment.NewLine}{exception}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Crashing anyway; nothing more to do.
        }
    }
}
