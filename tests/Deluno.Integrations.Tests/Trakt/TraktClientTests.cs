using System.Net;
using System.Net.Http.Json;
using System.Text;
using Deluno.Integrations.Trakt;

namespace Deluno.Integrations.Tests.Trakt;

/// <summary>
/// #846: the device-code OAuth exchange and refresh, exercised against a
/// fake HTTP handler standing in for api.trakt.tv.
/// </summary>
public sealed class TraktClientTests
{
    [Fact]
    public async Task StartDeviceAuthorizationAsync_returns_the_code_and_verification_url()
    {
        var handler = new RecordingHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                device_code = "device-abc",
                user_code = "AB12CD",
                verification_url = "https://trakt.tv/activate",
                expires_in = 600,
                interval = 5
            })
        });
        var client = new TraktClient(new SingleClientFactory(handler));

        var code = await client.StartDeviceAuthorizationAsync("client-id", CancellationToken.None);

        Assert.Equal("device-abc", code.DeviceCode);
        Assert.Equal("AB12CD", code.UserCode);
        Assert.Equal("https://trakt.tv/activate", code.VerificationUrl);
        Assert.Equal(600, code.ExpiresInSeconds);
        Assert.Equal(5, code.IntervalSeconds);
        Assert.Contains("/oauth/device/code", handler.LastRequestUri);
    }

    [Fact]
    public async Task PollDeviceTokenAsync_returns_connected_with_tokens_once_approved()
    {
        var handler = new RecordingHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                access_token = "access-1",
                refresh_token = "refresh-1",
                expires_in = 7776000
            })
        });
        var client = new TraktClient(new SingleClientFactory(handler));

        var result = await client.PollDeviceTokenAsync("client-id", "client-secret", "device-abc", CancellationToken.None);

        Assert.Equal(TraktDevicePollOutcome.Connected, result.Outcome);
        Assert.Equal("access-1", result.Tokens!.AccessToken);
        Assert.Equal("refresh-1", result.Tokens.RefreshToken);
        Assert.Equal(7776000, result.Tokens.ExpiresInSeconds);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, TraktDevicePollOutcome.Pending)]
    [InlineData((HttpStatusCode)429, TraktDevicePollOutcome.Pending)]
    [InlineData(HttpStatusCode.Gone, TraktDevicePollOutcome.Expired)]
    [InlineData((HttpStatusCode)418, TraktDevicePollOutcome.Denied)]
    [InlineData(HttpStatusCode.NotFound, TraktDevicePollOutcome.Denied)]
    public async Task PollDeviceTokenAsync_maps_every_trakt_polling_status(HttpStatusCode status, TraktDevicePollOutcome expected)
    {
        var handler = new RecordingHandler(request => new HttpResponseMessage(status));
        var client = new TraktClient(new SingleClientFactory(handler));

        var result = await client.PollDeviceTokenAsync("client-id", "client-secret", "device-abc", CancellationToken.None);

        Assert.Equal(expected, result.Outcome);
        Assert.Null(result.Tokens);
    }

    [Fact]
    public async Task RefreshAsync_returns_a_fresh_token_pair_on_success()
    {
        var handler = new RecordingHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                access_token = "access-2",
                refresh_token = "refresh-2",
                expires_in = 7776000
            })
        });
        var client = new TraktClient(new SingleClientFactory(handler));

        var tokens = await client.RefreshAsync("client-id", "client-secret", "refresh-1", CancellationToken.None);

        Assert.NotNull(tokens);
        Assert.Equal("access-2", tokens!.AccessToken);
        Assert.Equal("refresh-2", tokens.RefreshToken);
        Assert.Contains("/oauth/token", handler.LastRequestUri);
    }

    [Fact]
    public async Task RefreshAsync_returns_null_when_the_refresh_token_no_longer_works()
    {
        var handler = new RecordingHandler(request => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var client = new TraktClient(new SingleClientFactory(handler));

        var tokens = await client.RefreshAsync("client-id", "client-secret", "revoked-refresh", CancellationToken.None);

        Assert.Null(tokens);
    }

    [Fact]
    public async Task GetAsync_sends_the_trakt_headers_and_bearer_token()
    {
        HttpRequestMessage? captured = null;
        var handler = new RecordingHandler(request =>
        {
            captured = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json")
            };
        });
        var client = new TraktClient(new SingleClientFactory(handler));

        var body = await client.GetAsync("client-id", "access-1", "/users/me/watchlist", CancellationToken.None);

        Assert.Equal("[]", body);
        Assert.Equal("client-id", captured!.Headers.GetValues("trakt-api-key").Single());
        Assert.Equal("2", captured.Headers.GetValues("trakt-api-version").Single());
        Assert.Equal("Bearer", captured.Headers.Authorization!.Scheme);
        Assert.Equal("access-1", captured.Headers.Authorization.Parameter);
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public string? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri?.ToString();
            return Task.FromResult(respond(request));
        }
    }
}
