using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Security.Principal;

namespace Schedulry.Windows;

/// <summary>Things that need administrator rights, asked for through the normal UAC prompt.</summary>
public static class Elevation
{
    const int Cancelled = 1223;   // ERROR_CANCELLED: the person said No at the UAC prompt.

    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>Starts this app again as administrator. False when the person declined the prompt.</summary>
    public static bool StartElevatedCopy(IEnumerable<string> args)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        try
        {
            Process.Start(start);
            return true;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == Cancelled)
        {
            return false;
        }
    }

    /// <summary>
    /// Runs one change as administrator: a hidden copy of this app, started with
    /// <paramref name="args"/>, which makes it and exits (see Program.Main). False when the person
    /// declined the prompt; throws when the change failed.
    /// </summary>
    public static async Task<bool> RunElevatedAsync(params string[] args)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        try
        {
            using var process = Process.Start(start);
            if (process is null)
                return false;
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Windows didn't take the change (code 0x{process.ExitCode:X8}).");
            return true;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == Cancelled)
        {
            return false;
        }
    }

    /// <summary>
    /// Turns on Task Scheduler's history log. Directly when already elevated; otherwise through
    /// wevtutil with a UAC prompt. True when the log is on afterwards.
    /// </summary>
    public static async Task<bool> EnableHistoryAsync()
    {
        if (IsElevated)
        {
            using var config = new EventLogConfiguration(WindowsSchedulerHost.HistoryLog);
            config.IsEnabled = true;
            config.SaveChanges();
        }
        else
        {
            var wevtutil = Path.Combine(Environment.SystemDirectory, "wevtutil.exe");
            var start = new ProcessStartInfo(wevtutil, $"set-log {WindowsSchedulerHost.HistoryLog} /enabled:true")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            try
            {
                using var process = Process.Start(start);
                if (process is null)
                    return false;
                await process.WaitForExitAsync();
            }
            catch (Win32Exception e) when (e.NativeErrorCode == Cancelled)
            {
                return false;
            }
        }
        using var check = new EventLogConfiguration(WindowsSchedulerHost.HistoryLog);
        return check.IsEnabled;
    }
}
