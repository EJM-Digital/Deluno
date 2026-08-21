using Deluno.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Net;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Mounts the real <see cref="ApiVersioning.UseDelunoApiVersioning"/>
/// middleware in front of a trivial endpoint, so these assert against the
/// exact code Program.cs runs rather than a re-implementation of it.
/// </summary>
public sealed class ApiVersioningTests : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly TestServer _server;
    private readonly HttpClient _client;

    public ApiVersioningTests()
    {
        var builder = new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services => services.AddRouting())
                    .Configure(app =>
                    {
                        app.UseDelunoApiVersioning();
                        app.UseRouting();
                        app.UseDelunoApiUnmatchedPathGuard();
                        app.UseEndpoints(endpoints =>
                        {
                            endpoints.MapGet("/api/health/live", () => Results.Ok(new { status = "ok" }));
                        });
                        app.Run(async context =>
                        {
                            context.Response.StatusCode = StatusCodes.Status200OK;
                            await context.Response.WriteAsync("SPA fallback");
                        });
                    });
            });

        _host = builder.Build();
        _host.StartAsync().GetAwaiter().GetResult();
        _server = _host.GetTestServer();
        _client = _server.CreateClient();
    }

    [Fact]
    public async Task Bare_path_and_v1_alias_resolve_to_the_same_handler()
    {
        var bareResponse = await _client.GetAsync("/api/health/live");
        var versionedResponse = await _client.GetAsync("/api/v1/health/live");

        Assert.Equal(HttpStatusCode.OK, bareResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, versionedResponse.StatusCode);
        Assert.Equal(await bareResponse.Content.ReadAsStringAsync(), await versionedResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Every_response_carries_the_version_header()
    {
        var response = await _client.GetAsync("/api/health/live");

        Assert.True(response.Headers.TryGetValues("X-Deluno-Api-Version", out var values));
        Assert.Equal("v1", Assert.Single(values!));
    }

    [Fact]
    public async Task Unsupported_version_returns_400_not_404()
    {
        var response = await _client.GetAsync("/api/v9/health/live");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Unsupported API version", body);
        Assert.Contains("v1", body);
    }

    [Fact]
    public async Task Removed_versioned_api_path_returns_404_instead_of_spa_html()
    {
        var response = await _client.GetAsync("/api/v1/import-recovery/movies/test-case");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }
}
