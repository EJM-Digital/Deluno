using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Deluno.Api.Network;
using Deluno.Persistence.Tests.Support;
using Deluno.Security.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Host;

/// <summary>
/// Platform AUT-16 (the System page shows whether other devices can reach Deluno and Allow opens the way) over the real routes, and
/// AUT-14 (Allowed Hosts forced off by a config key when the web page is out of reach) on a real listener with the shared pipeline.
/// </summary>
public sealed class OtherDevicesAndAllowedHostsOnARealHostTests
{
    /// <summary>The operating system's firewall, standing for itself: blocked until Allow is pressed, then open.</summary>
    private sealed class ScriptedLanAccess : ILanAccess
    {
        public int Allowed { get; private set; }

        public Task<LanAccessStatus> GetStatusAsync(CancellationToken cancellationToken)
            => Task.FromResult(Allowed == 0
                ? new LanAccessStatus(LanAccessStates.Blocked, 5099, "Deluno 5099", "Other devices cannot reach Deluno until you allow port 5099.", CanAllow: true)
                : new LanAccessStatus(LanAccessStates.Open, 5099, "Deluno 5099", "Other devices can reach Deluno on port 5099.", CanAllow: false));

        public async Task<LanAccessStatus> AllowAsync(CancellationToken cancellationToken)
        {
            Allowed++;
            return await GetStatusAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task The_system_page_status_says_other_devices_are_turned_away_until_Allow_and_only_the_system_scope_can_press_it()
    {
        var firewall = new ScriptedLanAccess();
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => services.AddSingleton<ILanAccess>(firewall));
        var created = await (await app.Client.PostAsJsonAsync("/api/api-keys", new CreateApiKeyRequest("Reader", "read"))).Content.ReadFromJsonAsync<CreatedApiKeyResponse>();
        var reader = app.CreateAnonymousClient();
        reader.DefaultRequestHeaders.Add("X-Api-Key", created!.ApiKey);

        var before = await app.Client.GetFromJsonAsync<LanAccessStatus>("/api/system/lan-access");
        Assert.Equal(LanAccessStates.Blocked, before!.State);
        Assert.True(before.CanAllow);
        Assert.Contains("5099", before.Message, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/system/lan-access")).StatusCode);

        var refused = await reader.PostAsync("/api/system/lan-access/allow", content: null);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(0, firewall.Allowed);

        var allowed = await app.Client.PostAsync("/api/system/lan-access/allow", content: null);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal(LanAccessStates.Open, (await allowed.Content.ReadFromJsonAsync<LanAccessStatus>())!.State);
        Assert.Equal(LanAccessStates.Open, (await app.Client.GetFromJsonAsync<LanAccessStatus>("/api/system/lan-access"))!.State);
        Assert.Equal(1, firewall.Allowed);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateAnonymousClient().PostAsync("/api/system/lan-access/allow", content: null)).StatusCode);
    }

    private static async Task<HttpStatusCode> StatusFromHostNamedAsync(HttpClient client, string host)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/bootstrap-status");
        request.Headers.Host = host;
        return (await client.SendAsync(request)).StatusCode;
    }

    private static async Task TurnAllowedHostsOnAsync(SharedPipelineHost host)
    {
        var created = await host.Client.PostAsJsonAsync("/api/auth/bootstrap", new BootstrapUserRequest("owner", "Owner", "Deluno-Test-2026!"));
        host.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await created.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken);
        // Saved from the address that stays allowed, as the page insists.
        using var save = new HttpRequestMessage(HttpMethod.Put, "/api/security/allowed-hosts")
        {
            Content = JsonContent.Create(new UpdateAllowedHostsRequest(true, ["deluno.lan"]))
        };
        save.Headers.Host = "deluno.lan";
        Assert.Equal(HttpStatusCode.OK, (await host.Client.SendAsync(save)).StatusCode);
    }

    [Fact]
    public async Task Allowed_hosts_turns_away_an_address_not_on_the_list_and_the_recovery_config_key_lets_every_address_in_without_touching_the_saved_list()
    {
        await using var enforcing = await SharedPipelineHost.StartAsync();
        await TurnAllowedHostsOnAsync(enforcing);
        Assert.Equal(HttpStatusCode.OK, await StatusFromHostNamedAsync(enforcing.Client, "deluno.lan"));
        Assert.Equal(HttpStatusCode.BadRequest, await StatusFromHostNamedAsync(enforcing.Client, "other.lan"));

        await using var recovered = await SharedPipelineHost.StartAsync(new Dictionary<string, string?> { ["Security:AllowedHosts:ForceDisabled"] = "true" });
        await TurnAllowedHostsOnAsync(recovered);
        Assert.Equal(HttpStatusCode.OK, await StatusFromHostNamedAsync(recovered.Client, "other.lan"));
        Assert.Equal(HttpStatusCode.OK, await StatusFromHostNamedAsync(recovered.Client, "deluno.lan"));
        // The saved list is still what was saved: it was never edited to get in.
        var saved = await recovered.Services.GetRequiredService<Deluno.Security.Data.IAllowedHostsRepository>().GetAsync(CancellationToken.None);
        Assert.True(saved.Enabled);
        Assert.Equal(["deluno.lan"], saved.AllowedHosts);
    }
}
