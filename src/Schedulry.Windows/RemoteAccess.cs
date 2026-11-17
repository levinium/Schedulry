using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Schedulry.Core;

namespace Schedulry.Windows;

/// <summary>
/// Lets other computers manage this one's tasks by turning on two Windows Firewall rule groups:
/// Remote Scheduled Tasks Management (the tasks) and Remote Event Log Management (their run history).
/// Windows ships both turned off, and another computer then gets "The request is not supported"
/// (0x80070032). The groups go by Windows' own resource names, so they match in any language.
/// Signing in from another computer still takes an administrator account on this one.
/// </summary>
public sealed class RemoteAccess : IRemoteAccess
{
    /// <summary>The switch for an elevated copy of the app: "--allow-remote on" or "--allow-remote off".</summary>
    public const string Flag = "--allow-remote";

    static readonly string[] Groups =
    [
        "@FirewallAPI.dll,-33252",   // Remote Scheduled Tasks Management
        "@FirewallAPI.dll,-29252",   // Remote Event Log Management
    ];
    const int AllProfiles = 0x7FFFFFFF;   // NET_FW_PROFILE2_ALL: domain, private and public
    const int Cancelled = 1223;          // ERROR_CANCELLED: No at the UAC prompt

    public bool IsOn
    {
        get
        {
            try
            {
                dynamic policy = Policy();
                // The tasks group is the one that decides whether another computer can connect.
                return policy.IsRuleGroupCurrentlyEnabled(Groups[0]);
            }
            catch (COMException)
            {
                return false;
            }
        }
    }

    public async Task<bool> SetAsync(bool on)
    {
        if (Elevation.IsElevated)
        {
            Apply(on);
            return true;
        }
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        start.ArgumentList.Add(Flag);
        start.ArgumentList.Add(on ? "on" : "off");
        try
        {
            using var process = Process.Start(start);
            if (process is null)
                return false;
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Windows Firewall didn't take the change (code {process.ExitCode}).");
            return true;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == Cancelled)
        {
            return false;
        }
    }

    /// <summary>Makes the change. Needs administrator rights; the elevated copy calls this and exits.</summary>
    public static void Apply(bool on)
    {
        dynamic policy = Policy();
        foreach (var group in Groups)
            policy.EnableRuleGroup(AllProfiles, group, on);
    }

    /// <summary>
    /// Runs "--allow-remote on|off" when the app was started for it, before anything else.
    /// Returns the exit code, or null when the arguments ask for something else.
    /// </summary>
    public static int? RunFromArgs(string[] args)
    {
        var at = Array.FindIndex(args, a => a.Equals(Flag, StringComparison.OrdinalIgnoreCase));
        if (at < 0)
            return null;
        var on = at + 1 < args.Length && args[at + 1].Equals("on", StringComparison.OrdinalIgnoreCase);
        try
        {
            Apply(on);
            return 0;
        }
        catch (Exception e) when (e is COMException or UnauthorizedAccessException)
        {
            return e.HResult == 0 ? 1 : e.HResult;
        }
    }

    static object Policy() =>
        Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!)!;
}
