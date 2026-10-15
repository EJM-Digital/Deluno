using System.Text.Json.Nodes;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;
using static Deluno.Integrations.Tests.DownloadClients.RecordedClientCalls;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1301: whether SABnzbd 5.1.3 and NZBGet 26.3 have a news server to download from, read from their own recorded configuration
/// and from the same configuration with the server taken out or switched off, and what Deluno says when the client does not give a
/// configuration at all (a refused key, a refused login).
/// </summary>
public sealed class UsenetProviderCheckOnRecordingsTests
{
    private static HttpMessageHandler Sabnzbd(Func<HttpRequestMessage, string, HttpResponseMessage?>? intercept = null)
        => RecordedReplies.Handler("sabnzbd", [RecordedReplies.Sabnzbd("settings"), RecordedReplies.Sabnzbd("version")], intercept);

    private static HttpMessageHandler Nzbget(Func<HttpRequestMessage, string, HttpResponseMessage?>? intercept = null)
        => RecordedReplies.Handler("nzbget", [RecordedReplies.Nzbget("settings"), RecordedReplies.Nzbget("version")], intercept);

    [Fact]
    public async Task A_sabnzbd_that_has_a_news_server_in_its_recorded_configuration_has_a_provider()
    {
        var recorded = RecordedReplies.Sabnzbd("settings").Find("get_config").Reply.Json()["config"]!["servers"]!.AsArray();
        Assert.Contains(recorded, server => server!["enable"]!.GetValue<int>() == 1);

        var has = await new SabnzbdDownloadClient(new Factory(Sabnzbd())).HasUsenetProviderAsync(Client("sabnzbd", null, "api-key"), CancellationToken.None);

        Assert.True(has);
    }

    [Fact]
    public async Task A_sabnzbd_whose_configuration_has_no_servers_has_no_provider()
    {
        var handler = Sabnzbd((request, _) => request.RequestUri!.Query.Contains("mode=get_config", StringComparison.Ordinal)
            ? RecordedReplies.Json(RecordedReplies.Sabnzbd("settings").Body("mode=get_config", ("config.servers", new JsonArray())))
            : null);

        var has = await new SabnzbdDownloadClient(new Factory(handler)).HasUsenetProviderAsync(Client("sabnzbd", null, "api-key"), CancellationToken.None);

        Assert.False(has);
    }

    [Fact]
    public async Task A_sabnzbd_that_refuses_the_key_never_says_there_is_or_is_not_a_provider()
    {
        var refused = RecordedReplies.Sabnzbd("api-key-refused").Exchanges[0].Reply;
        var handler = Sabnzbd((request, _) => request.RequestUri!.Query.Contains("mode=get_config", StringComparison.Ordinal) ? refused.ToResponse() : null);

        var outcome = await Record.ExceptionAsync(async () =>
        {
            var has = await new SabnzbdDownloadClient(new Factory(handler)).HasUsenetProviderAsync(Client("sabnzbd", null, "wrong-key"), CancellationToken.None);
            Assert.Null(has);
        });

        Assert.True(outcome is null or ClientCredentialsRefusedException, outcome?.ToString());
    }

    [Fact]
    public async Task An_nzbget_with_an_active_server_in_its_recorded_configuration_has_a_provider()
    {
        var config = RecordedReplies.Nzbget("settings").Find("\"config\"").Reply.Json()["result"]!.AsArray();
        Assert.Contains(config, entry => entry!["Name"]!.GetValue<string>() == "Server1.Active" && entry["Value"]!.GetValue<string>() == "yes");

        var has = await new NzbGetDownloadClient(new Factory(Nzbget())).HasUsenetProviderAsync(Client("nzbget", "nzbget", "secret"), CancellationToken.None);

        Assert.True(has);
    }

    [Fact]
    public async Task An_nzbget_whose_only_server_is_switched_off_has_no_provider()
    {
        var handler = Nzbget((_, body) => body.Contains("\"config\"", StringComparison.Ordinal)
            ? RecordedReplies.Json(RecordedReplies.Nzbget("settings").Body("\"config\"", ("result.[Name=Server1.Active].Value", "no")))
            : null);

        var has = await new NzbGetDownloadClient(new Factory(handler)).HasUsenetProviderAsync(Client("nzbget", "nzbget", "secret"), CancellationToken.None);

        Assert.False(has);
    }

    [Fact]
    public async Task An_nzbget_that_refuses_the_login_never_says_there_is_or_is_not_a_provider()
    {
        var refused = RecordedReplies.Nzbget("login-refused").Exchanges[0].Reply;
        var handler = Nzbget((_, body) => body.Contains("\"config\"", StringComparison.Ordinal) ? refused.ToResponse() : null);

        var outcome = await Record.ExceptionAsync(async () =>
        {
            var has = await new NzbGetDownloadClient(new Factory(handler)).HasUsenetProviderAsync(Client("nzbget", "nzbget", "wrong"), CancellationToken.None);
            Assert.Null(has);
        });

        Assert.True(outcome is null or ClientCredentialsRefusedException, outcome?.ToString());
    }
}
