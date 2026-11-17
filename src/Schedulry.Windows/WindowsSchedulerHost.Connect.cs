using System.Diagnostics.Eventing.Reader;
using Schedulry.Core;

namespace Schedulry.Windows;

/// <summary>Opening another computer: checked straight away, with failures put into words.</summary>
public sealed partial class WindowsSchedulerHost
{
    const int BadNetPath = unchecked((int)0x80070035);
    const int BadNetName = unchecked((int)0x80070043);
    const int RpcUnavailable = unchecked((int)0x800706BA);
    const int RpcCallFailed = unchecked((int)0x800706BE);
    const int LogonFailure = unchecked((int)0x8007052E);
    const int PasswordExpired = unchecked((int)0x80070532);
    const int AccountLocked = unchecked((int)0x80070775);
    const int Timeout = unchecked((int)0x800705B4);
    const int NotSupported = unchecked((int)0x80070032);   // the target's firewall blocks remote task management

    /// <summary>
    /// Opens the computer the request names and makes one call, so a wrong name, a closed firewall or
    /// a refused account shows now rather than later. Names that mean this computer open it directly.
    /// Throws <see cref="InvalidOperationException"/> with a plain-words reason.
    /// </summary>
    public static WindowsSchedulerHost Connect(ConnectRequest request)
    {
        var name = ComputerNames.Normalise(request.Computer);
        if (ComputerNames.IsThisComputer(name, Environment.MachineName))
            return new WindowsSchedulerHost();

        var host = new WindowsSchedulerHost(name, request.Account, request.Password);
        try
        {
            using var root = host.Service.RootFolder;
            _ = root.GetTasks().Count;
        }
        catch (Exception e)
        {
            host.Dispose();
            throw new InvalidOperationException(Describe(e, name!, request.Account), e);
        }
        // The run history comes through the event log, a separate door in the other computer's
        // firewall. When it's closed the tasks still show, without their history.
        _ = host.HistoryEnabled;   // starts the log watcher when the history log can be read
        return host;
    }

    /// <summary>
    /// An account of the other computer itself: ".\name", "COMPUTER\name" (its short name), or a bare
    /// name, which Windows looks up there first. Not "DOMAIN\name" or "name@domain".
    /// </summary>
    public static bool IsLocalAccount(string? account, string computer)
    {
        account = account?.Trim();
        if (string.IsNullOrEmpty(account) || account.Contains('@'))
            return false;
        var slash = account.IndexOf('\\');
        if (slash < 0)
            return true;
        var domain = account[..slash];
        var shortName = computer.Split('.')[0];
        return domain == "." || domain.Equals(shortName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Why another computer couldn't be opened, in words someone can act on.</summary>
    public static string Describe(Exception e, string computer, string? account)
    {
        var who = string.IsNullOrWhiteSpace(account) ? "your account" : account.Trim();
        var code = e is UnauthorizedAccessException ? AccessDenied : e.HResult;
        // The code too, so a copied message says exactly what Windows answered.
        return $"{Words(e, code, computer, account, who)} (0x{code:X8})";
    }

    static string Words(Exception e, int code, string computer, string? account, string who) =>
        code switch
        {
            AccessDenied when IsLocalAccount(account, computer) =>
                $"{computer} refused {who}. If that account is an administrator there and the password is right (a wrong one gives this same message), " +
                $"Windows on {computer} is removing administrator rights from its own accounts when they sign in over the network. " +
                $"Connect with a domain account that's an administrator on {computer}, or allow its local administrators: open Schedulry on {computer}, " +
                $"go to Settings and turn on \"Also let this PC's own administrator accounts connect\".",
            AccessDenied =>
                $"{computer} refused {who}. Managing another computer's tasks takes an administrator account on that computer. Try connecting as another account.",
            LogonFailure => $"{computer} didn't accept that account name and password.",
            PasswordExpired => $"The password for {who} has expired. Change it, then connect again.",
            AccountLocked => $"{who} is locked out on {computer}.",
            BadNetPath or BadNetName => $"Couldn't find {computer} on the network. Check the name, or try its full name or IP address.",
            RpcUnavailable or RpcCallFailed or Timeout =>
                $"{computer} didn't answer. It may be switched off, or its firewall may block remote task management (the Windows Firewall rule group \"Remote Scheduled Tasks Management\").",
            NotSupported =>
                $"{computer} doesn't allow remote task management yet. To allow it, open Schedulry on {computer}, go to Settings and turn on \"Let other computers manage this PC's tasks\". Without Schedulry there, run this in an administrator PowerShell on {computer}: Enable-NetFirewallRule -DisplayGroup 'Remote Scheduled Tasks Management','Remote Event Log Management'",
            _ when e is EventLogException => $"Couldn't read {computer}'s event log: {e.Message}",
            _ => $"Couldn't connect to {computer}: {e.Message}",
        };
}
