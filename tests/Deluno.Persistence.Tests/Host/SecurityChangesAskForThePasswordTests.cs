using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;
using Deluno.Security;
using Deluno.Security.Contracts;

namespace Deluno.Persistence.Tests.Host;

/// <summary>
/// #1433: creating or deleting an API key, making a new set of recovery codes, changing the password and changing what the
/// home-network skip allows each ask for the current password, from a device the skip let in without one and from a signed-in
/// one, on the pipeline every host ships (a real listener, whose own loopback address is the "home network" device).
/// </summary>
public sealed class SecurityChangesAskForThePasswordTests
{
    private const string Username = "owner";
    private const string Password = "Deluno-Test-2026!";
    private const string Missing = "Enter your current password to make this change.";
    private const string Wrong = "Current password is not correct.";

    /// <summary>Who is asking: a device the skip let in with no token, or the owner signed in.</summary>
    public enum Who
    {
        SkippedDevice,
        SignedIn
    }

    private sealed record Rig(SharedPipelineHost Host, string Token);

    private static async Task<Rig> StartAsync()
    {
        var host = await SharedPipelineHost.StartAsync();
        var bootstrap = await host.Client.PostAsJsonAsync("/api/auth/bootstrap", new BootstrapUserRequest(Username, "Test Owner", Password));
        bootstrap.EnsureSuccessStatusCode();
        var login = await host.Client.PostAsJsonAsync("/api/auth/login", new LoginRequest(Username, Password));
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;
        var rig = new Rig(host, token);

        // Turn the skip on, as the person would, with the password; this computer's own address is on the home network.
        using var on = Send(rig, Who.SignedIn, HttpMethod.Put, "/api/security/local-network-auth", Skip(true), Password);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.SendAsync(on)).StatusCode);
        return rig;
    }

    private static UpdateLocalNetworkAuthRequest Skip(bool enabled, string[]? extra = null, string[]? allow = null, string[]? proxies = null)
        => new(enabled, extra ?? [], allow ?? [], proxies ?? []);

    private static HttpRequestMessage Send(Rig rig, Who who, HttpMethod method, string path, object? body, string? password)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (who == Who.SignedIn)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", rig.Token);
        }

        if (password is not null)
        {
            request.Headers.Add(CurrentPasswordGate.Header, password);
        }

        return request;
    }

    private static async Task<string?> RefusalAsync(HttpResponseMessage answer)
    {
        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
        var body = await answer.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("errors").GetProperty("currentPassword")[0].GetString();
    }

    private static async Task<string> MakeKeyAsync(Rig rig)
    {
        using var request = Send(rig, Who.SignedIn, HttpMethod.Post, "/api/api-keys", new { name = "Existing", scopes = "read" }, Password);
        using var answer = await rig.Host.Client.SendAsync(request);
        answer.EnsureSuccessStatusCode();
        return (await answer.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("item").GetProperty("id").GetString()!;
    }

    private static async Task<long> KeyCountAsync(Rig rig)
    {
        using var request = Send(rig, Who.SignedIn, HttpMethod.Get, "/api/api-keys", null, null);
        using var answer = await rig.Host.Client.SendAsync(request);
        return (await answer.Content.ReadFromJsonAsync<JsonElement>()).GetArrayLength();
    }

    [Theory]
    [InlineData(Who.SkippedDevice)]
    [InlineData(Who.SignedIn)]
    public async Task Creating_an_api_key_asks_for_the_password_and_a_wrong_one_is_refused_in_plain_words(Who who)
    {
        await using var host = (await StartAsync()).Host;
        var rig = new Rig(host, await TokenAsync(host));
        var body = new { name = "Reader", scopes = "read" };

        using (var none = Send(rig, who, HttpMethod.Post, "/api/api-keys", body, null))
        {
            Assert.Equal(Missing, await RefusalAsync(await host.Client.SendAsync(none)));
        }

        using (var wrong = Send(rig, who, HttpMethod.Post, "/api/api-keys", body, "not-the-password"))
        {
            Assert.Equal(Wrong, await RefusalAsync(await host.Client.SendAsync(wrong)));
        }

        Assert.Equal(0, await KeyCountAsync(rig));

        using var right = Send(rig, who, HttpMethod.Post, "/api/api-keys", body, Password);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.SendAsync(right)).StatusCode);
        Assert.Equal(1, await KeyCountAsync(rig));
    }

    [Theory]
    [InlineData(Who.SkippedDevice)]
    [InlineData(Who.SignedIn)]
    public async Task Deleting_an_api_key_asks_for_the_password_and_a_wrong_one_leaves_the_key(Who who)
    {
        var started = await StartAsync();
        await using var host = started.Host;
        var rig = new Rig(host, await TokenAsync(host));
        var id = await MakeKeyAsync(rig);

        using (var none = Send(rig, who, HttpMethod.Delete, $"/api/api-keys/{id}", null, null))
        {
            Assert.Equal(Missing, await RefusalAsync(await host.Client.SendAsync(none)));
        }

        using (var wrong = Send(rig, who, HttpMethod.Delete, $"/api/api-keys/{id}", null, "not-the-password"))
        {
            Assert.Equal(Wrong, await RefusalAsync(await host.Client.SendAsync(wrong)));
        }

        Assert.Equal(1, await KeyCountAsync(rig));

        using var right = Send(rig, who, HttpMethod.Delete, $"/api/api-keys/{id}", null, Password);
        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.SendAsync(right)).StatusCode);
        Assert.Equal(0, await KeyCountAsync(rig));
    }

    [Theory]
    [InlineData(Who.SkippedDevice)]
    [InlineData(Who.SignedIn)]
    public async Task Making_a_new_set_of_recovery_codes_asks_for_the_password_and_keeps_the_old_set_until_it_is_given(Who who)
    {
        await using var host = (await StartAsync()).Host;
        var rig = new Rig(host, await TokenAsync(host));

        using (var first = Send(rig, Who.SignedIn, HttpMethod.Post, "/api/auth/recovery-codes", null, Password))
        {
            Assert.Equal(HttpStatusCode.OK, (await host.Client.SendAsync(first)).StatusCode);
        }

        using (var none = Send(rig, who, HttpMethod.Post, "/api/auth/recovery-codes", null, null))
        {
            Assert.Equal(Missing, await RefusalAsync(await host.Client.SendAsync(none)));
        }

        using (var wrong = Send(rig, who, HttpMethod.Post, "/api/auth/recovery-codes", null, "not-the-password"))
        {
            Assert.Equal(Wrong, await RefusalAsync(await host.Client.SendAsync(wrong)));
        }

        using (var status = Send(rig, Who.SignedIn, HttpMethod.Get, "/api/auth/recovery-codes", null, null))
        {
            var kept = await (await host.Client.SendAsync(status)).Content.ReadFromJsonAsync<RecoveryCodesStatus>();
            Assert.Equal(new RecoveryCodesStatus(10, true), kept);
        }

        using var right = Send(rig, who, HttpMethod.Post, "/api/auth/recovery-codes", null, Password);
        var made = await host.Client.SendAsync(right);
        Assert.Equal(HttpStatusCode.OK, made.StatusCode);
        Assert.Equal(10, (await made.Content.ReadFromJsonAsync<RecoveryCodesMade>())!.Codes.Count);
    }

    [Theory]
    [InlineData(Who.SkippedDevice)]
    [InlineData(Who.SignedIn)]
    public async Task Changing_the_password_asks_for_the_current_one(Who who)
    {
        await using var host = (await StartAsync()).Host;
        var rig = new Rig(host, await TokenAsync(host));

        using (var wrong = Send(rig, who, HttpMethod.Put, "/api/auth/password", new ChangePasswordRequest("not-the-password", "a-brand-new-password"), null))
        {
            Assert.Equal(Wrong, await RefusalAsync(await host.Client.SendAsync(wrong)));
        }

        using (var blank = Send(rig, who, HttpMethod.Put, "/api/auth/password", new ChangePasswordRequest("", "a-brand-new-password"), null))
        {
            var answer = await host.Client.SendAsync(blank);
            Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
            Assert.Contains("Enter your current password.", await answer.Content.ReadAsStringAsync());
        }

        using var right = Send(rig, who, HttpMethod.Put, "/api/auth/password", new ChangePasswordRequest(Password, "a-brand-new-password"), null);
        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.SendAsync(right)).StatusCode);
        var login = await host.Client.PostAsJsonAsync("/api/auth/login", new LoginRequest(Username, "a-brand-new-password"));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    public static IEnumerable<object[]> SkipChanges()
    {
        yield return [Who.SkippedDevice, "switching it off", Skip(false)];
        yield return [Who.SignedIn, "switching it off", Skip(false)];
        yield return [Who.SkippedDevice, "adding a network", Skip(true, extra: ["10.20.0.0/16"])];
        yield return [Who.SignedIn, "adding a network", Skip(true, extra: ["10.20.0.0/16"])];
        yield return [Who.SkippedDevice, "narrowing it to an allow list", Skip(true, allow: ["127.0.0.1", "::1"])];
        yield return [Who.SignedIn, "narrowing it to an allow list", Skip(true, allow: ["127.0.0.1", "::1"])];
        yield return [Who.SkippedDevice, "trusting a proxy", Skip(true, proxies: ["10.0.0.5"])];
        yield return [Who.SignedIn, "trusting a proxy", Skip(true, proxies: ["10.0.0.5"])];
    }

    [Theory]
    [MemberData(nameof(SkipChanges))]
    public async Task Changing_what_the_home_network_skip_allows_asks_for_the_password_and_changes_nothing_without_it(Who who, string change, UpdateLocalNetworkAuthRequest request)
    {
        Assert.False(string.IsNullOrEmpty(change));
        await using var host = (await StartAsync()).Host;
        var rig = new Rig(host, await TokenAsync(host));

        using (var none = Send(rig, who, HttpMethod.Put, "/api/security/local-network-auth", request, null))
        {
            Assert.Equal(Missing, await RefusalAsync(await host.Client.SendAsync(none)));
        }

        using (var wrong = Send(rig, who, HttpMethod.Put, "/api/security/local-network-auth", request, "not-the-password"))
        {
            Assert.Equal(Wrong, await RefusalAsync(await host.Client.SendAsync(wrong)));
        }

        using (var read = Send(rig, Who.SignedIn, HttpMethod.Get, "/api/security/local-network-auth", null, null))
        {
            var unchanged = await (await host.Client.SendAsync(read)).Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(unchanged.GetProperty("enabled").GetBoolean());
            Assert.Empty(unchanged.GetProperty("additionalLocalNetworks").EnumerateArray());
            Assert.Empty(unchanged.GetProperty("allowList").EnumerateArray());
            Assert.Empty(unchanged.GetProperty("trustedProxies").EnumerateArray());
        }

        using var right = Send(rig, who, HttpMethod.Put, "/api/security/local-network-auth", request, Password);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.SendAsync(right)).StatusCode);
    }

    [Theory]
    [InlineData(Who.SkippedDevice)]
    [InlineData(Who.SignedIn)]
    public async Task Saving_the_skip_exactly_as_it_is_does_not_ask(Who who)
    {
        await using var host = (await StartAsync()).Host;
        var rig = new Rig(host, await TokenAsync(host));

        using var same = Send(rig, who, HttpMethod.Put, "/api/security/local-network-auth", Skip(true), null);

        Assert.Equal(HttpStatusCode.OK, (await host.Client.SendAsync(same)).StatusCode);
    }

    [Fact]
    public async Task Everything_else_stays_as_it_was_for_a_skipped_device()
    {
        await using var host = (await StartAsync()).Host;
        var rig = new Rig(host, await TokenAsync(host));

        foreach (var path in new[] { "/api/api-keys", "/api/security/local-network-auth", "/api/auth/recovery-codes", "/api/system/overview" })
        {
            using var read = Send(rig, Who.SkippedDevice, HttpMethod.Get, path, null, null);
            Assert.Equal(HttpStatusCode.OK, (await host.Client.SendAsync(read)).StatusCode);
        }
    }

    private static async Task<string> TokenAsync(SharedPipelineHost host)
    {
        var login = await host.Client.PostAsJsonAsync("/api/auth/login", new LoginRequest(Username, Password));
        login.EnsureSuccessStatusCode();
        return (await login.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;
    }
}
