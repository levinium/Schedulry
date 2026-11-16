using Avalonia;

namespace Schedulry.App;

internal static class Program
{
    /// <summary>The arguments the app started with, passed on when it restarts as administrator.</summary>
    internal static string[] Args { get; private set; } = [];

    [STAThread]
    public static void Main(string[] args)
    {
        Args = args;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrashLog(e.ExceptionObject);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
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
