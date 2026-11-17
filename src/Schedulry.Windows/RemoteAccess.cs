using System.Runtime.InteropServices;
using Microsoft.Win32;
using Schedulry.Core;

namespace Schedulry.Windows;

/// <summary>
/// Lets other computers manage this one's tasks.
/// <list type="bullet">
/// <item>The firewall: two Windows Firewall rule groups, Remote Scheduled Tasks Management (the
/// tasks) and Remote Event Log Management (their run history). Windows ships both turned off, and
/// another computer then gets "The request is not supported" (0x80070032). The groups go by
/// Windows' own resource names, so they match in any language.</item>
/// <item>Local administrators: Windows removes administrator rights from this PC's own accounts
/// when they sign in over the network, so ".\Admin" gets "Access is denied".
/// LocalAccountTokenFilterPolicy = 1 keeps them. Domain accounts never need it.</item>
/// </list>
/// Signing in from another computer always takes an administrator account on this one.
/// </summary>
public sealed class RemoteAccess : IRemoteAccess
{
    /// <summary>The switches for an elevated copy of the app, each followed by "on" or "off".</summary>
    public const string Flag = "--allow-remote";
    public const string LocalAdminsFlag = "--allow-local-admins";

    static readonly string[] Groups =
    [
        "@FirewallAPI.dll,-33252",   // Remote Scheduled Tasks Management
        "@FirewallAPI.dll,-29252",   // Remote Event Log Management
    ];
    const int AllProfiles = 0x7FFFFFFF;   // NET_FW_PROFILE2_ALL: domain, private and public
    const string PolicyKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
    const string TokenFilter = "LocalAccountTokenFilterPolicy";

    public bool IsOn
    {
        get
        {
            try
            {
                dynamic policy = FirewallPolicy();
                // The tasks group is the one that decides whether another computer can connect.
                return policy.IsRuleGroupCurrentlyEnabled(Groups[0]);
            }
            catch (COMException)
            {
                return false;
            }
        }
    }

    public bool LocalAdminsOn
    {
        get
        {
            using var key = Registry.LocalMachine.OpenSubKey(PolicyKey);
            return key?.GetValue(TokenFilter) is int value && value == 1;
        }
    }

    public Task<bool> SetAsync(bool on) => SetAsync(Flag, on, () => ApplyFirewall(on));

    public Task<bool> SetLocalAdminsAsync(bool on) => SetAsync(LocalAdminsFlag, on, () => ApplyLocalAdmins(on));

    /// <summary>Directly when already elevated; otherwise an elevated copy of the app makes the change.</summary>
    static async Task<bool> SetAsync(string flag, bool on, Action apply)
    {
        if (!Elevation.IsElevated)
            return await Elevation.RunElevatedAsync(flag, on ? "on" : "off");
        apply();
        return true;
    }

    /// <summary>Opens or closes the firewall rule groups. Needs administrator rights.</summary>
    public static void ApplyFirewall(bool on)
    {
        dynamic policy = FirewallPolicy();
        foreach (var group in Groups)
            policy.EnableRuleGroup(AllProfiles, group, on);
    }

    /// <summary>
    /// Keeps, or stops keeping, local administrators' rights over the network. Off removes the value,
    /// which is Windows' default. Needs administrator rights.
    /// </summary>
    public static void ApplyLocalAdmins(bool on)
    {
        using var key = Registry.LocalMachine.CreateSubKey(PolicyKey, writable: true);
        if (on)
            key.SetValue(TokenFilter, 1, RegistryValueKind.DWord);
        else
            key.DeleteValue(TokenFilter, throwOnMissingValue: false);
    }

    /// <summary>
    /// Runs "--allow-remote on|off" or "--allow-local-admins on|off" when the app was started for it,
    /// before anything else. Returns the exit code, or null when the arguments ask for something else.
    /// </summary>
    public static int? RunFromArgs(string[] args)
    {
        Action<bool>? apply = null;
        var at = Array.FindIndex(args, a => a.Equals(Flag, StringComparison.OrdinalIgnoreCase));
        if (at >= 0)
            apply = ApplyFirewall;
        else if ((at = Array.FindIndex(args, a => a.Equals(LocalAdminsFlag, StringComparison.OrdinalIgnoreCase))) >= 0)
            apply = ApplyLocalAdmins;
        if (apply is null)
            return null;
        var on = at + 1 < args.Length && args[at + 1].Equals("on", StringComparison.OrdinalIgnoreCase);
        try
        {
            apply(on);
            return 0;
        }
        catch (Exception e) when (e is COMException or UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return e.HResult == 0 ? 1 : e.HResult;
        }
    }

    static object FirewallPolicy() =>
        Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!)!;
}
