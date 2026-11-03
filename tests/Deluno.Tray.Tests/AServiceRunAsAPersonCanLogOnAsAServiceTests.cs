using Deluno.Tray;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>
/// On the golden VM, --install-service with a named user and the right password made the service and left it Stopped:
/// "This service account does not have the required user right Log on as a service" (service.named-user-runs, rc.30.37 run).
/// The service is now set up only after the account has that right, and when Windows will not give it the person is told why.
/// </summary>
public sealed class AServiceRunAsAPersonCanLogOnAsAServiceTests
{
    [Fact]
    public void The_account_is_given_the_right_before_the_service_is_set_to_use_it()
    {
        var asked = new List<string>();
        var old = ServiceManager.LogonRightGranter;
        try
        {
            ServiceManager.LogonRightGranter = account => { asked.Add(account); return null; };

            ServiceManager.GiveLogonRight(@".\owner");

            Assert.Equal(new[] { @".\owner" }, asked);
        }
        finally
        {
            ServiceManager.LogonRightGranter = old;
        }
    }

    [Fact]
    public void An_account_that_cannot_be_given_the_right_stops_the_setup_with_the_reason_in_words()
    {
        var old = ServiceManager.LogonRightGranter;
        try
        {
            ServiceManager.LogonRightGranter = _ => "Windows does not know an account called nobody.";

            var failure = Assert.Throws<InvalidOperationException>(() => ServiceManager.GiveLogonRight("nobody"));

            Assert.Equal("Deluno could not let nobody run a Windows service: Windows does not know an account called nobody.", failure.Message);
        }
        finally
        {
            ServiceManager.LogonRightGranter = old;
        }
    }

    [Fact]
    public void Windows_itself_is_asked_and_an_account_it_does_not_know_gets_a_plain_answer()
    {
        var problem = ServiceLogonRight.Grant(@".\no-such-account-" + Guid.NewGuid().ToString("N")[..8]);

        Assert.Equal(true, problem?.StartsWith("Windows does not know an account called "));
    }

    // service.named-user-runs (golden VM, after the right was granted): SCM event 7000 "Access is denied" - the account could not open
    // the program in the owner's profile folder or write the data there.
    [Fact]
    public void The_account_is_given_the_program_to_read_and_the_data_to_change_before_the_service_starts()
    {
        var asked = new List<(string Account, string Folder, System.Security.AccessControl.FileSystemRights Rights)>();
        var old = ServiceManager.FolderAccessGranter;
        try
        {
            ServiceManager.FolderAccessGranter = (account, folder, rights) => { asked.Add((account, folder, rights)); return null; };

            ServiceManager.GiveFolderAccess(@".\owner", @"C:\Users\me\AppData\Local\Deluno", @"C:\Users\me\AppData\Local\DelunoData");

            Assert.Equal(2, asked.Count);
            Assert.Equal(@"C:\Users\me\AppData\Local\Deluno", asked[0].Folder);
            Assert.False(asked[0].Rights.HasFlag(System.Security.AccessControl.FileSystemRights.WriteData));
            Assert.True(asked[0].Rights.HasFlag(System.Security.AccessControl.FileSystemRights.ExecuteFile));
            Assert.Equal(@"C:\Users\me\AppData\Local\DelunoData", asked[1].Folder);
            Assert.True(asked[1].Rights.HasFlag(System.Security.AccessControl.FileSystemRights.Modify));
        }
        finally
        {
            ServiceManager.FolderAccessGranter = old;
        }
    }

    [Fact]
    public void A_folder_the_account_cannot_be_given_stops_the_setup_with_the_reason_in_words()
    {
        var old = ServiceManager.FolderAccessGranter;
        try
        {
            ServiceManager.FolderAccessGranter = (_, folder, _) => $"no access to {folder}";

            var failure = Assert.Throws<InvalidOperationException>(() => ServiceManager.GiveFolderAccess("nobody", "prog", "data"));

            Assert.Equal("Deluno could not let nobody run a Windows service: no access to prog", failure.Message);
        }
        finally
        {
            ServiceManager.FolderAccessGranter = old;
        }
    }

    [Fact]
    public void Windows_itself_adds_the_rule_to_the_folder_and_to_files_made_there_later()
    {
        var folder = Path.Combine(Path.GetTempPath(), "deluno-acl-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(folder);
            var problem = ServiceFolderAccess.Grant(@"BUILTIN\Guests", folder, System.Security.AccessControl.FileSystemRights.ReadAndExecute);
            Assert.Null(problem);
            var file = Path.Combine(folder, "a.txt");
            File.WriteAllText(file, "x");
            var guests = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinGuestsSid, null);
            var rules = new FileInfo(file).GetAccessControl().GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)).Cast<System.Security.AccessControl.FileSystemAccessRule>();
            Assert.Contains(rules, r => r.IdentityReference == guests);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
