using System.Security.AccessControl;
using System.Security.Principal;

namespace Deluno.Tray;

/// <summary>
/// Lets a Windows account that will run Deluno as a service open the program and its data. A person's own profile folder is
/// closed to other accounts, so a service set to run as someone else stopped at once with "Access is denied" (SCM event 7000,
/// golden VM, service.named-user-runs) even with the right password and the log on as a service right.
/// </summary>
internal static class ServiceFolderAccess
{
    /// <summary>Gives the account the rights on the folder and everything in it, now and later; returns the reason in plain words when it could not.</summary>
    internal static string? Grant(string account, string folder, FileSystemRights rights)
    {
        try
        {
            var name = account.StartsWith(@".\", StringComparison.Ordinal) ? Environment.MachineName + account[1..] : account;
            var sid = (SecurityIdentifier)new NTAccount(name).Translate(typeof(SecurityIdentifier));
            Directory.CreateDirectory(folder);
            var info = new DirectoryInfo(folder);
            var security = info.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(sid, rights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            info.SetAccessControl(security);
            return null;
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or UnauthorizedAccessException or IOException or ArgumentException or SystemException)
        {
            return $"Windows would not let {account} use the folder {folder} ({ex.Message}).";
        }
    }
}
