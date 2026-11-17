using Avalonia;
using Microsoft.Toolkit.Uwp.Notifications;
using Velopack;

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
        // First: when the installer, an update or the uninstaller runs the app to do its part, this does it and exits.
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => Uninstalling())
            .Run();

        // An elevated copy started only to make one change (the firewall, the sign-in task): it does that and exits.
        if ((Windows.RemoteAccess.RunFromArgs(args) ?? Windows.StartupRegistration.RunFromArgs(args)) is int code)
        {
            Environment.ExitCode = code;
            return;
        }

        bool Has(string flag) => args.Contains(flag, StringComparer.OrdinalIgnoreCase);
        StartInTray = Has(TrayFlag);
        // Windows adds -ToastActivated when it starts the app for a notification click.
        Args = args.Where(a => !a.Equals(TrayFlag, StringComparison.OrdinalIgnoreCase)
            && !a.Equals(RestartedFlag, StringComparison.OrdinalIgnoreCase)
            && !a.Equals("-ToastActivated", StringComparison.OrdinalIgnoreCase)).ToArray();
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrashLog(e.ExceptionObject);
        MoveOldData();

        // The demo computer runs beside the real one, so it has its own name.
        if (!SingleInstance.Claim(Demo ? "Schedulry.Demo" : "Schedulry", quiet: StartInTray, waitForPrevious: Has(RestartedFlag)))
            return;
        // Always run as administrator: this copy hands over to an elevated one, which waits for it to close.
        // No at the prompt opens it normally.
        if (WantsAdmin() && Windows.Elevation.StartElevatedCopy(
                args.Where(a => !a.Equals("-ToastActivated", StringComparison.OrdinalIgnoreCase)
                    && !a.Equals(RestartedFlag, StringComparison.OrdinalIgnoreCase)).Append(RestartedFlag)))
            return;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
    }

    /// <summary>The "Always run as administrator" setting is on and this copy isn't. Never for the demo.</summary>
    static bool WantsAdmin()
    {
        if (Demo || Windows.Elevation.IsElevated)
            return false;
        try
        {
            return new Core.JsonSettingsStore(Path.Combine(DataFolder, "settings.json")).Load().AlwaysRunAsAdmin;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Named exactly this because Avalonia's XAML previewer looks it up by convention.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    /// <summary>
    /// The person's settings, stars, groups and saved computers. Not in %LOCALAPPDATA%\Schedulry:
    /// the installer puts the program there, and uninstalling removes that folder whole.
    /// </summary>
    internal static string DataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Schedulry");

    /// <summary>Copies before 0.1.0 kept their files where the program now goes; they move once.</summary>
    static void MoveOldData()
    {
        var old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Schedulry");
        try
        {
            if (Directory.Exists(DataFolder) || !Directory.Exists(old))
                return;
            Directory.CreateDirectory(DataFolder);
            foreach (var file in Directory.EnumerateFiles(old, "*.json"))
                File.Move(file, Path.Combine(DataFolder, Path.GetFileName(file)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not moved: the app starts with fresh settings rather than not at all.
        }
    }

    /// <summary>
    /// Before the uninstaller removes the program: nothing of it is left behind in Windows. The
    /// person's own files in %APPDATA%\Schedulry stay, so a reinstall finds their stars and groups.
    /// </summary>
    static void Uninstalling()
    {
        try
        {
            new Windows.StartupRegistration().RemoveAll();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }
        try
        {
            // Its notifications, and the registration that lets Windows show them.
            ToastNotificationManagerCompat.Uninstall();
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException)
        {
        }
    }

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
