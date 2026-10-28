using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// <c>GET /api/health/ready</c> on the real host with no sign-in: 503 with the checks while a check fails (a fresh install has
/// no worker heartbeat yet), 200 with the same body once every check passes. The container's health check asks this route.
/// </summary>
public sealed class ReadinessRouteTests
{
    [Fact]
    public async Task The_route_answers_503_with_the_failing_check_while_the_worker_has_not_started_and_200_once_it_has()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var anonymous = app.CreateAnonymousClient();

        var notReady = await anonymous.GetAsync("/api/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, notReady.StatusCode);
        var before = await notReady.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(before.GetProperty("ready").GetBoolean());
        Assert.Equal("not_ready", before.GetProperty("status").GetString());
        var failing = before.GetProperty("checks").EnumerateArray().Where(check => check.GetProperty("status").GetString() != "ready").ToArray();
        var heartbeat = Assert.Single(failing);
        Assert.Contains("heartbeat", heartbeat.GetProperty("name").GetString());

        await app.Services.GetRequiredService<IJobQueueRepository>().HeartbeatAsync("worker-test", CancellationToken.None);
        var ready = await anonymous.GetAsync("/api/health/ready");

        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        var after = await ready.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(after.GetProperty("ready").GetBoolean());
        Assert.Equal("ready", after.GetProperty("status").GetString());
        Assert.All(after.GetProperty("checks").EnumerateArray(), check => Assert.Equal("ready", check.GetProperty("status").GetString()));
        Assert.True(after.TryGetProperty("checkedUtc", out _));
    }
}
