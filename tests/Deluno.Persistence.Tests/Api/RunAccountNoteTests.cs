using Deluno.Filesystem;

namespace Deluno.Persistence.Tests.Api;

/// <summary>#1141: a folder-permission note names the account Deluno really runs as.</summary>
public sealed class RunAccountNoteTests
{
    [Fact]
    public void A_tray_install_runs_as_the_signed_in_windows_user_not_a_service_account()
    {
        var phrase = RunAccount.Phrase(windows: true, interactive: true, accountName: @"PC\jo");

        Assert.Contains(@"Windows user", phrase);
        Assert.Contains(@"PC\jo", phrase);
        Assert.DoesNotContain("service account", phrase);
    }

    [Fact]
    public void A_service_install_names_the_service_account()
    {
        var phrase = RunAccount.Phrase(windows: true, interactive: false, accountName: @"NT AUTHORITY\LocalService");

        Assert.Contains("service account", phrase);
        Assert.Contains("LocalService", phrase);
    }

    [Fact]
    public void A_host_that_is_not_windows_never_talks_about_a_windows_service()
    {
        var phrase = RunAccount.Phrase(windows: false, interactive: false, accountName: "deluno");

        Assert.DoesNotContain("Windows", phrase);
        Assert.DoesNotContain("service account", phrase);
    }
}
