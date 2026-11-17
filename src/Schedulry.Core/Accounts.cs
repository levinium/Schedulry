namespace Schedulry.Core;

/// <summary>Account names as Task Scheduler stores them.</summary>
public static class Accounts
{
    /// <summary>
    /// Whether two names are the same account. Windows often stores a task's account without its
    /// domain ("avery.lee") while the signed-in name has one ("CONTOSO\avery.lee", "AzureAD\avery.lee"),
    /// so a bare name matches the same name in any domain. Two different domains never match.
    /// </summary>
    public static bool Same(string? a, string? b)
    {
        a = a?.Trim();
        b = b?.Trim();
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            return false;
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            return true;
        var (domainA, nameA) = Split(a);
        var (domainB, nameB) = Split(b);
        return (domainA is null || domainB is null) && string.Equals(nameA, nameB, StringComparison.OrdinalIgnoreCase);
    }

    static (string? Domain, string Name) Split(string account)
    {
        var slash = account.LastIndexOf('\\');
        if (slash >= 0)
            return (account[..slash], account[(slash + 1)..]);
        var at = account.IndexOf('@');
        return at > 0 ? (account[(at + 1)..], account[..at]) : (null, account);
    }
}
