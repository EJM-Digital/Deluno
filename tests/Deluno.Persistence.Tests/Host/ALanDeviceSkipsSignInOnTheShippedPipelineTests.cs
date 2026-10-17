using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Deluno.Persistence.Tests.Support;
using Deluno.Security.Contracts;

namespace Deluno.Persistence.Tests.Host;

/// <summary>
/// "Skip sign-in on the home network" for a device that is not this computer, on the pipeline every host ships (a real Kestrel
/// listener, a real data folder, the sign-in gate in front of every route): the device's address comes through a trusted proxy's
/// X-Forwarded-For, a home-network address is let in with no token, an outside address is not, and an allow-list narrows who counts.
/// </summary>
public sealed class ALanDeviceSkipsSignInOnTheShippedPipelineTests
{
    private const string Username = "owner";
    private const string Password = "Deluno-Test-2026!";

    private static async Task<string> SignInAsync(SharedPipelineHost host)
    {
        var bootstrap = await host.Client.PostAsJsonAsync("/api/auth/bootstrap", new BootstrapUserRequest(Username, "Test Owner", Password));
        Assert.True(bootstrap.IsSuccessStatusCode || bootstrap.StatusCode == HttpStatusCode.Conflict);
        var login = await host.Client.PostAsJsonAsync("/api/auth/login", new LoginRequest(Username, Password));
        login.EnsureSuccessStatusCode();
        return (await login.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;
    }

    private static async Task SaveAsync(SharedPipelineHost host, string token, UpdateLocalNetworkAuthRequest request)
    {
        using var save = new HttpRequestMessage(HttpMethod.Put, "/api/security/local-network-auth") { Content = JsonContent.Create(request) };
        save.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var answer = await host.Client.SendAsync(save);
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
    }

    private static async Task<HttpStatusCode> AskAsFromAsync(SharedPipelineHost host, string address)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/system/overview");
        request.Headers.Add("X-Forwarded-For", address);
        using var answer = await host.Client.SendAsync(request);
        return answer.StatusCode;
    }

    [Fact]
    public async Task A_device_on_the_home_network_behind_a_trusted_proxy_is_let_in_and_one_from_outside_is_not()
    {
        await using var host = await SharedPipelineHost.StartAsync();
        var token = await SignInAsync(host);
        await SaveAsync(host, token, new UpdateLocalNetworkAuthRequest(true, [], [], ["127.0.0.1", "::1"]));

        Assert.Equal(HttpStatusCode.OK, await AskAsFromAsync(host, "192.168.1.50"));
        Assert.Equal(HttpStatusCode.Unauthorized, await AskAsFromAsync(host, "8.8.8.8"));
    }

    [Fact]
    public async Task An_allow_list_lets_in_only_the_listed_devices_and_turning_the_setting_off_turns_every_device_away()
    {
        await using var host = await SharedPipelineHost.StartAsync();
        var token = await SignInAsync(host);

        // This computer is a local address. An allow list that does not name it turns it away; one that does lets it in.
        await SaveAsync(host, token, new UpdateLocalNetworkAuthRequest(true, [], ["192.168.1.50"], []));
        Assert.Equal(HttpStatusCode.Unauthorized, await AskAsync(host));

        await SaveAsync(host, token, new UpdateLocalNetworkAuthRequest(true, [], ["127.0.0.1", "::1", "::ffff:127.0.0.1"], []));
        Assert.Equal(HttpStatusCode.OK, await AskAsync(host));

        await SaveAsync(host, token, new UpdateLocalNetworkAuthRequest(false, [], ["127.0.0.1", "::1", "::ffff:127.0.0.1"], []));
        Assert.Equal(HttpStatusCode.Unauthorized, await AskAsync(host));
    }

    private static async Task<HttpStatusCode> AskAsync(SharedPipelineHost host)
    {
        using var answer = await host.Client.GetAsync("/api/system/overview");
        return answer.StatusCode;
    }

    [Fact]
    public void The_computer_a_request_really_came_from_is_the_socket_peer_not_a_forwarded_claim()
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("127.0.0.1");
        context.Items[Deluno.Security.Hardening.LocalNetworkAuthConstants.OriginalRemoteAddressItemKey] = IPAddress.Parse("203.0.113.9");

        Assert.Equal(IPAddress.Parse("203.0.113.9"), Deluno.Security.Hardening.LocalNetworkAuthConstants.SocketPeer(context));
        Assert.False(Deluno.Filesystem.FilesystemEndpointRouteBuilderExtensions.IsFromThisMachine(
            Deluno.Security.Hardening.LocalNetworkAuthConstants.SocketPeer(context), null, context.Request.Headers));
    }
}
