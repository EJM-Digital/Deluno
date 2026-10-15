using System.Net;
using System.Net.Http.Json;
using Deluno.Persistence.Tests.Support;
using Deluno.Security.Contracts;

namespace Deluno.Persistence.Tests.Host;

/// <summary>
/// Platform AUT-04 (login throttling) and FRS-09 (the first account is made through the open bootstrap route), proved on the real
/// shared request pipeline over a real Kestrel listener with no account yet.
/// </summary>
public sealed class LoginThrottlingAndFirstVisitorTests
{
    [Fact]
    public async Task The_eleventh_sign_in_attempt_in_a_minute_from_one_address_is_429_with_a_retry_after_and_the_tenth_is_not()
    {
        await using var host = await SharedPipelineHost.StartAsync();

        for (var attempt = 1; attempt <= 10; attempt++)
        {
            var response = await host.Client.PostAsJsonAsync("/api/auth/login", new LoginRequest("nobody", "wrong-password-" + attempt));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        var throttled = await host.Client.PostAsJsonAsync("/api/auth/login", new LoginRequest("nobody", "wrong-password-11"));

        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.True(throttled.Headers.RetryAfter?.Delta > TimeSpan.Zero, "A 429 names how long to wait.");
        Assert.Contains("Rate limit exceeded", await throttled.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_limit_is_the_configured_one_and_the_page_load_status_check_is_never_counted_against_it()
    {
        await using var host = await SharedPipelineHost.StartAsync(new Dictionary<string, string?>
        {
            ["Security:Login:PermitLimit"] = "3",
            ["Security:Login:WindowSeconds"] = "60"
        });

        for (var poll = 0; poll < 8; poll++)
        {
            Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/api/auth/bootstrap-status")).StatusCode);
        }

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await host.Client.PostAsJsonAsync("/api/auth/login", new LoginRequest("nobody", "wrong-password-" + attempt))).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests,
            (await host.Client.PostAsJsonAsync("/api/auth/login", new LoginRequest("nobody", "wrong-password-4"))).StatusCode);
        // The throttle is the login's own: the status check still answers.
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/api/auth/bootstrap-status")).StatusCode);
    }

    [Fact]
    public async Task On_a_fresh_install_anyone_who_reaches_the_port_is_asked_to_set_up_and_can_make_the_first_account_with_no_credentials_only_once()
    {
        await using var host = await SharedPipelineHost.StartAsync();

        var status = await host.Client.GetFromJsonAsync<BootstrapStatusResponse>("/api/auth/bootstrap-status");
        Assert.True(status!.RequiresSetup);

        // Reached from another computer: a forwarded address is not an owner and is not turned away.
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/bootstrap")
        {
            Content = JsonContent.Create(new BootstrapUserRequest("first", "First Person", "a-long-password-1"))
        };
        request.Headers.Add("X-Forwarded-For", "192.168.1.50");
        var created = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var login = await created.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.Equal("first", login!.User.Username);

        // After that the same open route is closed to everyone.
        var second = await host.Client.PostAsJsonAsync("/api/auth/bootstrap", new BootstrapUserRequest("intruder", "Intruder", "another-long-password-2"));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.False((await host.Client.GetFromJsonAsync<BootstrapStatusResponse>("/api/auth/bootstrap-status"))!.RequiresSetup);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync("/api/system/overview")).StatusCode);
    }
}
