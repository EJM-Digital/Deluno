using System.Net;
using Deluno.Api.Calendar;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Media;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Security;
using Deluno.Security.Contracts;
using Deluno.Security.Data;
using Deluno.Series.Contracts;
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
    private readonly IMovieCatalogRepository _movies;
    private readonly ISeriesCatalogRepository _series;
    private readonly IMediaTagStore _tags;
    private readonly FixedTimeProvider _timeProvider;

    public CalendarFeedEndpointTests()
    {
        _storage = TestStorage.Create();
        _timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-25T09:00:00Z"));
        var migrator = new SqliteDatabaseMigrator(_storage.Factory, _timeProvider);

        new PlatformSchemaInitializer(_storage.Factory, migrator, NullLogger<PlatformSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        new SeriesSchemaInitializer(_storage.Factory, migrator, NullLogger<SeriesSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        new MoviesSchemaInitializer(_storage.Factory, migrator, NullLogger<MoviesSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None).GetAwaiter().GetResult();

        _security = new SqliteSecurityRepository(_storage.Factory, _timeProvider);
        _series = new SqliteSeriesCatalogRepository(_storage.Factory, _timeProvider);
        _movies = new SqliteMovieCatalogRepository(_storage.Factory, _timeProvider);
        _tags = new SqliteMediaTagStore(_storage.Factory, _timeProvider);

        var builder = new HostBuilder().ConfigureWebHost(webBuilder =>
        {
            webBuilder
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddSingleton<TimeProvider>(_timeProvider);
                    services.AddSingleton(_security);
                    services.AddSingleton(_series);
                    services.AddSingleton(_movies);
                    services.AddSingleton<IPlatformSettingsRepository>(
                        new SqlitePlatformSettingsRepository(
                            _storage.Factory,
                            _timeProvider,
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

    /// <summary>
    /// Radarr/Sonarr's own calendar hides an unmonitored title unless you ask
    /// otherwise — the feed matches that default rather than dumping
    /// everything Deluno has ever heard of into a subscribed calendar (#786).
    /// </summary>
    [Fact]
    public async Task Feed_hides_an_unmonitored_movie_unless_asked_to_include_it()
    {
        var key = await CreateKeyAsync("read");
        var digitalRelease = DateOnly.FromDateTime(_timeProvider.GetUtcNow().AddDays(5).UtcDateTime);

        var wanted = await _movies.AddAsync(new CreateMovieRequest("Wanted Movie", 2026, null, Monitored: true), CancellationToken.None);
        await _movies.UpdateReleaseDatesAsync(wanted.Id, null, digitalRelease, null, CancellationToken.None);

        var skipped = await _movies.AddAsync(new CreateMovieRequest("Skipped Movie", 2026, null, Monitored: false), CancellationToken.None);
        await _movies.UpdateReleaseDatesAsync(skipped.Id, null, digitalRelease, null, CancellationToken.None);

        var defaultBody = await GetFeedAsync(key);
        Assert.Contains("Wanted Movie", defaultBody);
        Assert.DoesNotContain("Skipped Movie", defaultBody);

        var includingUnmonitored = await GetFeedAsync(key, "&unmonitored=true");
        Assert.Contains("Wanted Movie", includingUnmonitored);
        Assert.Contains("Skipped Movie", includingUnmonitored);
    }

    /// <summary>
    /// The tag filter (#786): only a title carrying one of the requested tags
    /// reaches the feed, the same "any of these" rule a saved filter's tag
    /// condition already uses.
    /// </summary>
    [Fact]
    public async Task Feed_narrows_to_movies_carrying_one_of_the_requested_tags()
    {
        var key = await CreateKeyAsync("read");
        var digitalRelease = DateOnly.FromDateTime(_timeProvider.GetUtcNow().AddDays(5).UtcDateTime);

        var tagged = await _movies.AddAsync(new CreateMovieRequest("Tagged Movie", 2026, null), CancellationToken.None);
        await _movies.UpdateReleaseDatesAsync(tagged.Id, null, digitalRelease, null, CancellationToken.None);
        await _tags.ReplaceAsync(MediaKind.Movie, tagged.Id, [new MediaTagAssignment("tag-1", "Keepers")], CancellationToken.None);

        var untagged = await _movies.AddAsync(new CreateMovieRequest("Untagged Movie", 2026, null), CancellationToken.None);
        await _movies.UpdateReleaseDatesAsync(untagged.Id, null, digitalRelease, null, CancellationToken.None);

        var body = await GetFeedAsync(key, $"&tag={Uri.EscapeDataString("Keepers")}");

        Assert.Contains("Tagged Movie", body);
        Assert.DoesNotContain("Untagged Movie", body);
    }

    /// <summary>Same rule, the episode side: an unmonitored show's episode stays off the feed by default.</summary>
    [Fact]
    public async Task Feed_hides_an_unmonitored_shows_episode_unless_asked_to_include_it()
    {
        var key = await CreateKeyAsync("read");
        var airDate = _timeProvider.GetUtcNow().AddDays(5);

        var show = await _series.AddAsync(new CreateSeriesRequest("Skipped Show", 2026, null, Monitored: false), CancellationToken.None);
        await _series.SyncEpisodeCatalogueAsync(
            show.Id,
            [new CatalogueEpisodeItem(1, 1, "Pilot", null, airDate)],
            "tmdb",
            CancellationToken.None);

        var defaultBody = await GetFeedAsync(key);
        Assert.DoesNotContain("Skipped Show", defaultBody);

        var includingUnmonitored = await GetFeedAsync(key, "&unmonitored=true");
        Assert.Contains("Skipped Show", includingUnmonitored);
    }

    private async Task<string> GetFeedAsync(string apiKey, string extra = "")
    {
        var response = await _client.GetAsync($"/api/calendar/feed.ics?apikey={Uri.EscapeDataString(apiKey)}{extra}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
        _storage.Dispose();
    }
}
