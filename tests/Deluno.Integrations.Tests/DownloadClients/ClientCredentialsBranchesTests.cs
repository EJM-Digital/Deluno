using System.Net;
using Deluno.Connections;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>#1251: a client refusing the key or login is said plainly, naming the credential that protocol takes.</summary>
public sealed class ClientCredentialsBranchesTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static DownloadClientItem Client(string protocol, string? username = null)
        => new("c1", "My Client", protocol, "localhost", 8080, username, null, null, false, null, "movies", "tv", null, 1, true, "healthy", null, null, null, Now, Now, Now);

    [Theory]
    [InlineData("sabnzbd", "SABnzbd", DownloadClientCredential.ApiKey)]
    [InlineData("SABnzbd", "SABnzbd", DownloadClientCredential.ApiKey)]
    [InlineData("deluge", "Deluge", DownloadClientCredential.Password)]
    [InlineData("qbittorrent", "qBittorrent", DownloadClientCredential.ApiKey)]
    [InlineData("nzbget", "NZBGet", DownloadClientCredential.UsernameAndPassword)]
    [InlineData("transmission", "Transmission", DownloadClientCredential.UsernameAndPassword)]
    [InlineData("utorrent", "uTorrent", DownloadClientCredential.UsernameAndPassword)]
    [InlineData("rtorrent", "rTorrent", DownloadClientCredential.UsernameAndPassword)]
    [InlineData("mystery", "My Client", DownloadClientCredential.UsernameAndPassword)]
    public void Each_protocol_names_the_credential_it_takes(string protocol, string name, DownloadClientCredential kind)
    {
        Assert.Equal((name, kind), ClientCredentials.Describe(Client(protocol)));
    }

    [Theory]
    [InlineData(null, DownloadClientCredential.ApiKey)]
    [InlineData("", DownloadClientCredential.ApiKey)]
    [InlineData("  ", DownloadClientCredential.ApiKey)]
    [InlineData("admin", DownloadClientCredential.UsernameAndPassword)]
    public void qBittorrent_takes_an_API_key_only_when_no_username_is_set(string? username, DownloadClientCredential kind)
    {
        Assert.Equal(kind, ClientCredentials.Describe(Client("qbittorrent", username)).Kind);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, true)]
    [InlineData(HttpStatusCode.Forbidden, true)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.OK, false)]
    public void Only_a_401_or_403_is_a_refusal_in_every_overload(HttpStatusCode status, bool refused)
    {
        using var response = new HttpResponseMessage(status);
        var named = () => ClientCredentials.ThrowIfRefused(response, "Deluge", DownloadClientCredential.Password);
        var byClient = () => ClientCredentials.ThrowIfRefused(response, Client("deluge"));
        var byStatus = () => ClientCredentials.ThrowIfRefused(status, "Deluge", DownloadClientCredential.Password);

        foreach (var call in new[] { named, byClient, byStatus })
        {
            if (refused)
            {
                var thrown = Assert.Throws<ClientCredentialsRefusedException>(call);
                Assert.Equal("Deluge refused Deluno's password", thrown.Message[..32]);
            }
            else
            {
                call();
            }
        }
    }

    [Fact]
    public void No_status_at_all_is_not_a_refusal()
    {
        ClientCredentials.ThrowIfRefused((HttpStatusCode?)null, "Deluge", DownloadClientCredential.Password);
    }

    [Fact]
    public void A_refusal_is_the_plain_exception_or_an_http_failure_with_401_or_403_and_nothing_else()
    {
        Assert.True(ClientCredentials.IsRefusal(ClientCredentials.Refused("X", DownloadClientCredential.ApiKey)));
        Assert.True(ClientCredentials.IsRefusal(new HttpRequestException("x", null, HttpStatusCode.Unauthorized)));
        Assert.True(ClientCredentials.IsRefusal(new HttpRequestException("x", null, HttpStatusCode.Forbidden)));
        Assert.False(ClientCredentials.IsRefusal(new HttpRequestException("x", null, HttpStatusCode.BadGateway)));
        Assert.False(ClientCredentials.IsRefusal(new HttpRequestException("x")));
        Assert.False(ClientCredentials.IsRefusal(new InvalidOperationException("x")));
    }

    [Fact]
    public void The_sentence_for_a_failure_is_the_refusal_in_the_protocols_words_or_nothing()
    {
        var client = Client("sabnzbd");

        Assert.Equal(DownloadClientCredentialText.Refused("SABnzbd", DownloadClientCredential.ApiKey),
            ClientCredentials.MessageFor(client, new HttpRequestException("x", null, HttpStatusCode.Forbidden)));
        Assert.Equal("already said", ClientCredentials.MessageFor(client, new ClientCredentialsRefusedException("already said")));
        Assert.Null(ClientCredentials.MessageFor(client, new IOException("x")));
    }

    [Fact]
    public void A_refused_action_and_grab_fail_with_the_auth_kind_and_the_message()
    {
        var client = Client("deluge");
        var refused = ClientCredentials.Refused("Deluge", DownloadClientCredential.Password);
        var action = ClientCredentials.Failure(client, "hash", "pause", refused);
        var grab = ClientCredentials.GrabFailure(client, new DownloadClientGrabRequest("Release", "http://x/y", "movies", "movies", null, null, null), "no");

        Assert.False(action.Succeeded);
        Assert.Equal(refused.Message, action.Message);
        Assert.Equal(Deluno.Contracts.IntegrationFailureFactory.FromLegacy("download-client", "c1", "x", "y", "auth", "m").Kind, action.Failure!.Kind);
        Assert.False(grab.Succeeded);
        Assert.Equal("failed", grab.Status);
        Assert.Equal("no", grab.Message);
        Assert.NotNull(grab.Failure);
    }
}
