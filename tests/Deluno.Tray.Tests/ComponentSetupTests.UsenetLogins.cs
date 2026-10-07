using Deluno.Api.Network;
using Deluno.Tray;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>#1142: the picker asks for the Usenet clients' logins and the install writes them, instead of generated ones.</summary>
public sealed partial class ComponentSetupTests
{
    private const string NzbgetTemplate = "MainDir=${AppDir}\\downloads\r\nControlIP=0.0.0.0\r\nControlPort=6789\r\nControlUsername=nzbget\r\nControlPassword=tegbzn6789\r\nServer1.Active=yes\r\n";

    [Fact]
    public async Task The_login_chosen_for_NZBGet_is_written_to_its_settings_and_kept_to_connect()
    {
        var writer = new FakeConfigWriter(true);
        var store = new RecordingCredentialStore();

        await InstallWith(Load("nzbget"), writer, store, template: NzbgetTemplate, chosenLogin: new BundledComponentCredentials("james", "a-long-password"));

        Assert.Contains("ControlUsername=james\r\n", writer.Content);
        Assert.Contains("ControlPassword=a-long-password\r\n", writer.Content);
        Assert.DoesNotContain("tegbzn6789", writer.Content);
        Assert.Equal(new BundledComponentCredentials("james", "a-long-password"), store.Saved["nzbget"]);
    }

    [Fact]
    public async Task The_login_chosen_for_SABnzbd_is_written_to_its_settings_and_its_api_key_is_still_what_Deluno_uses()
    {
        var writer = new FakeConfigWriter(true);
        var store = new RecordingCredentialStore();

        await InstallWith(Load("sabnzbd"), writer, store, chosenLogin: new BundledComponentCredentials("james", "a-long-password"));

        Assert.Contains("[misc]\nusername = \"james\"\npassword = \"a-long-password\"\n", writer.Content!.Replace("\r\n", "\n"));
        // The key is only made when SABnzbd first runs; with none to read, nothing is kept as a connection login.
        Assert.False(store.Saved.TryGetValue("sabnzbd", out var kept) && kept.Password == "a-long-password");
    }

    [Fact]
    public async Task Without_a_chosen_login_NZBGet_keeps_the_login_it_ships_with()
    {
        var writer = new FakeConfigWriter(true);

        await InstallWith(Load("nzbget"), writer, new RecordingCredentialStore(), template: NzbgetTemplate);

        Assert.Contains("ControlUsername=nzbget", writer.Content);
        Assert.Contains("ControlPassword=tegbzn6789", writer.Content);
    }

    [Fact]
    public async Task A_login_that_cannot_be_put_into_the_settings_says_so_instead_of_pretending()
    {
        var writer = new FakeConfigWriter(true);

        var result = await InstallWith(Load("nzbget"), writer, new RecordingCredentialStore(), template: "ControlIP=0.0.0.0\r\n", chosenLogin: new BundledComponentCredentials("james", "a-long-password"));

        Assert.Equal(ComponentInstallOutcome.InstalledNeedsSetup, result.Outcome);
        Assert.DoesNotContain("a-long-password", result.Message);
    }

    [Fact]
    public void Same_for_both_gives_NZBGet_the_SABnzbd_login()
    {
        var (logins, problem) = UsenetLogins.Resolve(true, true, true, "james", "a-long-password", "", "");

        Assert.Null(problem);
        Assert.Equal(new BundledComponentCredentials("james", "a-long-password"), logins["sabnzbd"]);
        Assert.Equal(logins["sabnzbd"], logins["nzbget"]);
    }

    [Fact]
    public void Separate_logins_stay_separate_and_an_unticked_client_is_not_asked()
    {
        var (logins, problem) = UsenetLogins.Resolve(true, true, false, "sabuser", "sab-password", "nzbuser", "nzb-password");
        Assert.Null(problem);
        Assert.Equal("nzbuser", logins["nzbget"].Username);

        var (onlySab, onlySabProblem) = UsenetLogins.Resolve(true, false, false, "sabuser", "sab-password", "", "");
        Assert.Null(onlySabProblem);
        Assert.Single(onlySab);
    }

    [Theory]
    [InlineData("", "a-long-password")]
    [InlineData("james", "")]
    [InlineData("ab", "a-long-password")]
    [InlineData("james", "short")]
    [InlineData("james", " padded-password ")]
    [InlineData("james", "has\"quote-password")]
    [InlineData("james", "has$dollar-password")]
    public void A_login_that_is_empty_too_short_or_unsafe_is_refused_without_repeating_it(string username, string password)
    {
        var (_, problem) = UsenetLogins.Resolve(true, false, false, username, password, "", "");

        Assert.NotNull(problem);
        Assert.DoesNotContain("a-long-password", problem);
        if (password.Length > 0)
        {
            Assert.DoesNotContain(password, problem);
        }
    }

    [Fact]
    public void A_ticked_client_with_no_login_blocks_the_install()
    {
        var (_, problem) = UsenetLogins.Resolve(false, true, false, "", "", "", "");

        Assert.Contains("NZBGet", problem);
    }

    [Fact]
    public void A_credentials_record_never_prints_its_password()
    {
        Assert.DoesNotContain("a-long-password", new BundledComponentCredentials("james", "a-long-password").ToString());
    }
}
