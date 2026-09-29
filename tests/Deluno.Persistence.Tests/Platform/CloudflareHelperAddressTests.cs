using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Integrations.Search.Trackers;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Platform;

/// <summary>#1064: the Cloudflare helper's address must be a web address, in Settings and in the test button.</summary>
public sealed class CloudflareHelperAddressTests
{
    [Theory]
    [InlineData("ftp://127.0.0.1:8191")]
    [InlineData("file:///etc/passwd")]
    [InlineData("127.0.0.1:8191")]
    [InlineData("gopher://host/")]
    public async Task Settings_refuse_a_helper_address_that_is_not_http_or_https(string address)
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PatchAsJsonAsync("/api/settings", new { cloudflareHelperUrl = address });

        Assert.False(response.IsSuccessStatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("errors").TryGetProperty("cloudflareHelperUrl", out var message));
        Assert.Contains("http://", message[0].GetString());
    }

    [Fact]
    public async Task Settings_keep_an_http_helper_address()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PatchAsJsonAsync("/api/settings", new { cloudflareHelperUrl = "http://127.0.0.1:8191" });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_test_button_refuses_a_non_web_address_without_touching_the_network()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsJsonAsync("/api/trackers/cloudflare-helper/test", new { url = "ftp://127.0.0.1:21" });

        Assert.True(response.IsSuccessStatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("healthy").GetBoolean());
        Assert.Contains("http://", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task The_probe_refuses_a_non_web_address_instead_of_throwing()
    {
        using var http = new HttpClient(new NeverCalledHandler());

        var (healthy, message) = await CloudflareHelperProbe.CheckAsync(http, new Uri("ftp://127.0.0.1:21"), CancellationToken.None);

        Assert.False(healthy);
        Assert.Contains("http://", message);
    }

    private sealed class NeverCalledHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("The probe must not send a request to a non-web address.");
    }
}
