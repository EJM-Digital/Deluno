using System.Threading.RateLimiting;
using Deluno.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Net;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Mounts a rate limiter with the same shape as Program.cs's global API
/// limiter — <see cref="ApiRateLimitPartitionKeyResolver"/> plus a fixed
/// window — but a tiny permit limit, so the 429 path is exercised without
/// hammering a real endpoint hundreds of times.
/// </summary>
public sealed class ApiRateLimitTests : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly TestServer _server;
    private readonly HttpClient _client;

    public ApiRateLimitTests()
    {
        var builder = new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddRouting();
                        services.AddRateLimiter(options =>
                        {
                            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
                                RateLimitPartition.GetFixedWindowLimiter(
                                    ApiRateLimitPartitionKeyResolver.Resolve(httpContext),
                                    _ => new FixedWindowRateLimiterOptions
                                    {
                                        PermitLimit = 2,
                                        Window = TimeSpan.FromMinutes(1),
                                        QueueLimit = 0
                                    }));
                            options.OnRejected = async (context, cancellationToken) =>
                            {
                                context.HttpContext.Response.Headers.RetryAfter = "60";
                                context.HttpContext.Response.ContentType = "application/json";
                                await context.HttpContext.Response.WriteAsync(
                                    "{\"error\":\"Rate limit exceeded.\",\"retryAfterSeconds\":60}",
                                    cancellationToken);
                            };
                        });
                    })
                    .Configure(app =>
                    {
                        app.UseRateLimiter();
                        app.UseRouting();
                        app.UseEndpoints(endpoints =>
                        {
                            endpoints.MapGet("/api/health/live", () => Results.Ok(new { status = "ok" }));
                        });
                    });
            });

        _host = builder.Build();
        _host.StartAsync().GetAwaiter().GetResult();
        _server = _host.GetTestServer();
        _client = _server.CreateClient();
    }

    [Fact]
    public async Task Requests_over_the_permit_limit_are_rejected_with_retry_after()
    {
        var first = await _client.GetAsync("/api/health/live");
        var second = await _client.GetAsync("/api/health/live");
        var third = await _client.GetAsync("/api/health/live");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);

        Assert.True(third.Headers.TryGetValues("Retry-After", out var retryAfter));
        Assert.Equal("60", Assert.Single(retryAfter!));

        var body = await third.Content.ReadAsStringAsync();
        Assert.Contains("Rate limit exceeded", body);
    }

    [Fact]
    public async Task Different_api_keys_get_independent_budgets()
    {
        var callerA1 = await SendWithApiKeyAsync("key-a");
        var callerA2 = await SendWithApiKeyAsync("key-a");
        var callerA3 = await SendWithApiKeyAsync("key-a");
        var callerB1 = await SendWithApiKeyAsync("key-b");

        Assert.Equal(HttpStatusCode.OK, callerA1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, callerA2.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, callerA3.StatusCode);
        Assert.Equal(HttpStatusCode.OK, callerB1.StatusCode);
    }

    private async Task<HttpResponseMessage> SendWithApiKeyAsync(string apiKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/health/live");
        request.Headers.Add("X-Api-Key", apiKey);
        return await _client.SendAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }
}
