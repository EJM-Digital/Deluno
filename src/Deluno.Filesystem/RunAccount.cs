using System.Runtime.InteropServices;

namespace Deluno.Filesystem;

/// <summary>
/// Names the account Deluno actually runs as, so a folder-permission note tells the person which
/// account to give access to (#1141). A tray install runs as the signed-in Windows user; only a
/// service install has a service account.
/// </summary>
public static class RunAccount
{
    /// <summary>For example "the Windows user you are signed in as (PC\jo)" or "the Deluno service account (NT AUTHORITY\LocalService)".</summary>
    public static string Phrase()
        => Phrase(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), Environment.UserInteractive, AccountName());

    public static string Phrase(bool windows, bool interactive, string? accountName)
    {
        var named = string.IsNullOrWhiteSpace(accountName) ? string.Empty : $" ({accountName})";
        if (!windows)
        {
            return $"the user Deluno runs as{named}";
        }

        return interactive
            ? $"the Windows user Deluno runs as{named}"
            : $"the Deluno service account{named}";
    }

    private static string? AccountName()
    {
        try
        {
            return string.IsNullOrWhiteSpace(Environment.UserDomainName)
                ? Environment.UserName
                : Environment.UserDomainName + "\\" + Environment.UserName;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
