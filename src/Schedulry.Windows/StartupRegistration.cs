using Microsoft.Win32;
using Schedulry.Core;

namespace Schedulry.Windows;

/// <summary>
/// Starting with Windows: a value in the person's own Run key (no administrator rights needed),
/// which starts this program in the notification area when they sign in. It shows, and can be
/// turned off, in Task Manager's Startup apps.
/// </summary>
public sealed class StartupRegistration(string valueName = "Schedulry") : IStartup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>What the Run value holds: this program, quoted, told to start in the notification area.</summary>
    public static string Command => $"\"{Environment.ProcessPath}\" --tray";

    public bool IsOn
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(valueName) is string;
        }
    }

    public void Set(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (on)
            key.SetValue(valueName, Command, RegistryValueKind.String);
        else
            key.DeleteValue(valueName, throwOnMissingValue: false);
    }
}
