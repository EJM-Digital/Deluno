using System.Net;
using Deluno.Api.Calendar;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Security;
using Deluno.Security.Contracts;
using Deluno.Security.Data;
using Deluno.Series.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The calendar feed is the one endpoint that takes its key from the query
/// string, because no calendar client can send a header (#260). That makes it
/// worth pinning what it accepts: a read-scoped key and nothing else.
/// </summary>
public sealed class CalendarFeedEndpointTests : IAsyncDisposable
{
    private readonly TestStorage _storage;
    private readonly IHost _host;
    private readonly HttpClient _client;
    private readonly ISecurityRepository _security;

    public CalendarFeedEndpointTests()
    {
        _storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-25T09:00:00Z"));
        var migrator = new SqliteDatabaseMigrator(_storage.Factory, timeProvider);

        new PlatformSchemaInitializer(_storage.Factory, migrator, NullLogger<PlatformSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        new SeriesSchemaInitializer(_storage.Factory, migrator, NullLogger<SeriesSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        new MoviesSchemaInitializer(_storage.Factory, migrator, NullLogger<MoviesSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None).GetAwaiter().GetResult();

        _security = new SqliteSecurityRepository(_storage.Factory, timeProvider);

        var builder = new HostBuilder().ConfigureWebHost(webBuilder =>
        {
            webBuilder
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddSingleton<TimeProvider>(timeProvider);
                    services.AddSingleton(_security);
                    services.AddSingleton<ISeriesCatalogRepository>(
                        new SqliteSeriesCatalogRepository(_storage.Factory, timeProvider));
                    services.AddSingleton<IMovieCatalogRepository>(
                        new SqliteMovieCatalogRepository(_storage.Factory, timeProvider));
                    services.AddSingleton<IPlatformSettingsRepository>(
                        new SqlitePlatformSettingsRepository(
                            _storage.Factory,
                            timeProvider,
                            TestSecretProtection.Create(_storage)));
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapDelunoCalendarFeedEndpoints());
                });
        });

        _host = builder.Build();
        _host.StartAsync().GetAwaiter().GetResult();
        _client = _host.GetTestServer().CreateClient();
    }

    private async Task<string> CreateKeyAsync(string scopes)
    {
        var created = await _security.CreateApiKeyAsync(
            new CreateApiKeyRequest($"Calendar {scopes}", scopes),
            CancellationToken.None);
        return created.ApiKey;
    }

    [Fact]
    public async Task Feed_refuses_a_request_with_no_key_at_all()
    {
        var response = await _client.GetAsync("/api/calendar/feed.ics");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Feed_refuses_a_key_it_has_never_issued()
    {
        var response = await _client.GetAsync("/api/calendar/feed.ics?apikey=deluno_not-a-real-key");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Feed_refuses_a_real_key_that_does_not_carry_the_read_scope()
    {
        var key = await CreateKeyAsync("queue");

        var response = await _client.GetAsync($"/api/calendar/feed.ics?apikey={Uri.EscapeDataString(key)}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Feed_serves_a_calendar_document_to_a_read_scoped_key()
    {
        var key = await CreateKeyAsync("read");

        var response = await _client.GetAsync($"/api/calendar/feed.ics?apikey={Uri.EscapeDataString(key)}");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/calendar", response.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("BEGIN:VCALENDAR", body);
        Assert.Contains("END:VCALENDAR", body);
        // A subscription that got cached would silently stop updating.
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? string.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
        _storage.Dispose();
    }
}
