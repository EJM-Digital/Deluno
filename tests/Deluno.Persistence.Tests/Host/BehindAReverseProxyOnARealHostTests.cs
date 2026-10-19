using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Deluno.Persistence.Tests.Support;
using Deluno.Security.Contracts;
using Deluno.Security.Hardening;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Deluno.Persistence.Tests.Host;

/// <summary>
/// Platform AUT-17 and DOK-17: Deluno behind a reverse proxy. On a real listener with the shared pipeline, the host the proxy forwards is
/// the host Allowed Hosts judges, and it is remembered as a candidate for the list whether it was let in or turned away.
/// </summary>
public sealed class BehindAReverseProxyOnARealHostTests
{
    private static async Task<(HttpStatusCode Status, string Body)> AskAsync(HttpClient client, string host, string forwardedHost)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/bootstrap-status");
        request.Headers.Host = host;
        request.Headers.Add("X-Forwarded-Host", forwardedHost);
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-For", "203.0.113.7");
        var response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_host_a_proxy_forwards_is_the_host_Allowed_Hosts_judges_and_it_is_remembered_as_a_candidate_either_way()
    {
        await using var app = await SharedPipelineHost.StartAsync();
        var created = await app.Client.PostAsJsonAsync("/api/auth/bootstrap", new BootstrapUserRequest("owner", "Owner", "Deluno-Test-2026!"));
        app.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await created.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken);
        using var save = new HttpRequestMessage(HttpMethod.Put, "/api/security/allowed-hosts")
        {
            Content = JsonContent.Create(new UpdateAllowedHostsRequest(true, ["deluno.lan"]))
        };
        save.Headers.Host = "deluno.lan";
        Assert.Equal(HttpStatusCode.OK, (await app.Client.SendAsync(save)).StatusCode);

        // The proxy talks to Deluno by an internal name; the person's address is what it forwards.
        var listed = await AskAsync(app.Client, "deluno-internal", "deluno.lan");
        Assert.Equal(HttpStatusCode.OK, listed.Status);

        var unlisted = await AskAsync(app.Client, "deluno-internal", "stranger.example");
        Assert.Equal(HttpStatusCode.BadRequest, unlisted.Status);

        var seen = app.Services.GetRequiredService<IForwardedHostTracker>().Seen;
        Assert.Contains("deluno.lan", seen);
        Assert.Contains("stranger.example", seen);
    }

    [Fact]
    public async Task The_shared_pipeline_trusts_the_forwarded_for_proto_and_host_headers_of_a_proxy_on_the_same_machine()
    {
        await using var app = await SharedPipelineHost.StartAsync();

        var options = app.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        Assert.Equal(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost, options.ForwardedHeaders);
    }
}
