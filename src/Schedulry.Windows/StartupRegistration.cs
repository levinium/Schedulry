using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.TaskScheduler;
using Schedulry.Core;

namespace Schedulry.Windows;

/// <summary>
/// Starting with Windows, in the notification area when the person signs in. Two ways:
/// <list type="bullet">
/// <item>Normally a value in their own Run key (no administrator rights needed). It shows, and can
/// be turned off, in Task Manager's Startup apps.</item>
/// <item>When Schedulry always runs as administrator, a scheduled task that runs at their sign-in
/// with highest privileges, so there's no prompt at sign-in. Windows skips Run-key programs that
/// need administrator rights. Making that task needs administrator approval once.</item>
/// </list>
/// </summary>
public sealed class StartupRegistration(string valueName = "Schedulry") : IStartup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>The switch for an elevated copy: "--startup-task on|off ACCOUNT".</summary>
    public const string TaskFlag = "--startup-task";

    /// <summary>The scheduled task's name, in Task Scheduler's top folder.</summary>
    public string TaskName => $"{valueName} at sign-in";

    /// <summary>What the Run value holds: this program, quoted, told to start in the notification area.</summary>
    public static string Command => $"\"{Environment.ProcessPath}\" --tray";

    public bool IsOn => RunKeyOn || TaskOn;

    bool RunKeyOn
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(valueName) is string;
        }
    }

    bool TaskOn
    {
        get
        {
            try
            {
                using var service = new TaskService();
                using var task = service.GetTask(TaskName);
                return task is not null;
            }
            catch (Exception e) when (e is COMException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    public async System.Threading.Tasks.Task SetAsync(bool on, bool asAdmin)
    {
        var wantTask = on && asAdmin;
        var user = WindowsIdentity.GetCurrent().Name;
        if (TaskOn != wantTask)
        {
            bool done;
            if (Elevation.IsElevated)
            {
                SetTask(TaskName, wantTask, user, Environment.ProcessPath!);
                done = true;
            }
            else
            {
                done = await Elevation.RunElevatedAsync(TaskFlag, wantTask ? "on" : "off", user);
            }
            // No at the prompt: starting with Windows still works, through the Run key, and asks at sign-in.
            if (!done && wantTask)
            {
                SetRunKey(true);
                return;
            }
        }
        SetRunKey(on && !TaskOn);
    }

    /// <summary>
    /// For the uninstaller: the Run value goes, and the sign-in task too when Windows lets this copy
    /// remove it (it may need administrator rights; a task left behind then fails harmlessly at sign-in).
    /// </summary>
    public void RemoveAll()
    {
        SetRunKey(false);
        try
        {
            SetTask(TaskName, false, "", "");
        }
        catch (Exception e) when (e is COMException or UnauthorizedAccessException)
        {
        }
    }

    void SetRunKey(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (on)
            key.SetValue(valueName, Command, RegistryValueKind.String);
        else
            key.DeleteValue(valueName, throwOnMissingValue: false);
    }

    /// <summary>Makes or removes the sign-in task. Needs administrator rights.</summary>
    public static void SetTask(string name, bool on, string user, string program)
    {
        using var service = new TaskService();
        if (!on)
        {
            if (service.GetTask(name) is { } existing)
            {
                existing.Dispose();
                service.RootFolder.DeleteTask(name, exceptionOnNotExists: false);
            }
            return;
        }
        var definition = service.NewTask();
        definition.RegistrationInfo.Description = $"Starts Schedulry in the notification area, as administrator, when {user} signs in. Made by Schedulry's \"Start with Windows\".";
        definition.Principal.UserId = user;
        definition.Principal.LogonType = TaskLogonType.InteractiveToken;
        definition.Principal.RunLevel = TaskRunLevel.Highest;
        definition.Triggers.Add(new LogonTrigger { UserId = user });
        definition.Actions.Add(new ExecAction(program, "--tray"));
        definition.Settings.ExecutionTimeLimit = TimeSpan.Zero;
        definition.Settings.DisallowStartIfOnBatteries = false;
        definition.Settings.StopIfGoingOnBatteries = false;
        definition.Settings.MultipleInstances = TaskInstancesPolicy.IgnoreNew;
        service.RootFolder.RegisterTaskDefinition(name, definition);
    }

    /// <summary>
    /// Runs "--startup-task on|off ACCOUNT" when the app was started for it. Returns the exit code,
    /// or null when the arguments ask for something else.
    /// </summary>
    public static int? RunFromArgs(string[] args)
    {
        var at = Array.FindIndex(args, a => a.Equals(TaskFlag, StringComparison.OrdinalIgnoreCase));
        if (at < 0)
            return null;
        if (at + 2 >= args.Length)
            return 87;   // ERROR_INVALID_PARAMETER
        try
        {
            SetTask(new StartupRegistration().TaskName, args[at + 1].Equals("on", StringComparison.OrdinalIgnoreCase), args[at + 2], Environment.ProcessPath!);
            return 0;
        }
        catch (Exception e) when (e is COMException or UnauthorizedAccessException or ArgumentException)
        {
            return e.HResult == 0 ? 1 : e.HResult;
        }
    }
}
