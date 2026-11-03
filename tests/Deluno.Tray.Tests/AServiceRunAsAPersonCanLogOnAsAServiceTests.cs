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
}
