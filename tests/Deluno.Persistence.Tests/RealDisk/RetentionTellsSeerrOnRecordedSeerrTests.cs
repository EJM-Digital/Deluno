using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;
using Deluno.Retention;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// What a retention rule asks of Seerr and tells it (#522), on the whole application over a real library folder and the real databases, with
/// Seerr 3.5.0 answering over a real loopback port from its own recorded replies (tests/fixtures/recorded/seerr): the requests it holds and the
/// users it knows, the film it holds a record of, the 500 it gave for a TMDb id it could not retrieve and the 204 it gave to the delete.
/// </summary>
public sealed class RetentionTellsSeerrOnRecordedSeerrTests
{
    private static Recording Seerr(string state) => RecordedReplies.Seerr(state);

    /// <summary>A Seerr that holds Sintel's record (id 1), that lists one declined series request, and that is either answering or "unable to retrieve" the film.</summary>
    private sealed class Seerr3 : IAsyncDisposable
    {
        public bool Retrieving = true;
        public RecordedAppServer Server { get; private set; } = null!;

        public static async Task<Seerr3> StartAsync()
        {
            var self = new Seerr3();
            var server = await RecordedAppServer.StartAsync("seerr", [], (request, _) =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path == "/api/v1/request") return Seerr("request-declined-tv").Exchanges[2].Reply.ToResponse();
                if (path == "/api/v1/user") return Seerr("requests-empty").Exchanges[1].Reply.ToResponse();
                if (path == "/api/v1/movie/45745") return self.Retrieving ? Seerr("request-pending").Exchanges[3].Reply.ToResponse() : Seerr("lookup-unknown").Exchanges[2].Reply.ToResponse();
                if (request.Method == HttpMethod.Delete && path == "/api/v1/media/1") return Seerr("media-deleted").Exchanges[0].Reply.ToResponse();
                return null;
            });
            self.Server = server;
            return self;
        }

        public async ValueTask DisposeAsync() => await Server.DisposeAsync();
    }

    private static async Task<string> ConnectAsync(ApplicationTestHost app, string baseUrl, string name = "Seerr", bool enabled = true)
    {
        var saved = await app.Client.PostAsJsonAsync("/api/seerr-servers", new { name, baseUrl, apiKey = "0123456789abcdef0123456789abcdef", isEnabled = enabled });
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
        return (await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task A_title_a_rule_removed_that_Seerr_could_not_clear_is_listed_with_why_and_cleared_the_next_time_the_rule_is_checked_once_Seerr_answers()
    {
        await using var seerr = await Seerr3.StartAsync();
        await using var world = await RealDiskLibrary.OpenAsync(await ApplicationTestHost.StartAsync());
        var serverId = await ConnectAsync(world.App, seerr.Server.BaseUrl);
        var folder = Path.Combine(world.Root, "Sintel (2010)");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "Sintel (2010) [WEB 1080p].mkv");
        File.WriteAllBytes(file, Enumerable.Repeat((byte)0x42, 4096).ToArray());
        var added = await world.App.Client.PostAsJsonAsync("/api/movies/", new { title = "Sintel", releaseYear = 2010, imdbId = "tt1727587", monitored = true, libraryId = world.Library.Id, searchForMissingNow = false });
        var movieId = JsonDocument.Parse(await added.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
        await world.SqlAsync("movies", $"UPDATE movie_entries SET created_utc = '{DateTimeOffset.UtcNow.AddDays(-365):O}', metadata_provider = 'tmdb', metadata_provider_id = '45745' WHERE id = '{movieId}';");
        var rule = await world.App.Client.PostAsJsonAsync("/api/retention/rules", new
        {
            name = "Old films", level = "movie", isEnabled = true, steps = new[] { new { type = "condition", op = "keep", condition = "added:beyond:30" } },
            mediaServerIds = Array.Empty<string>(), removeFromServers = false, waitDays = 1, removalMode = "automatic", checkIntervalHours = 24, expiryAction = "delete"
        });
        var ruleId = JsonDocument.Parse(await rule.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
        Assert.Empty((await world.App.Client.GetFromJsonAsync<JsonElement>("/api/retention/seerr-cleanups")).EnumerateArray());

        // Seerr cannot retrieve the film just now (its recorded 500): the removal stands and the clearing is owed.
        seerr.Retrieving = false;
        await world.App.Client.PostAsync($"/api/retention/rules/{ruleId}/check", null);
        await world.SqlAsync("platform", $"UPDATE retention_marks SET remove_after_utc = '{DateTimeOffset.UtcNow.AddHours(-1):O}';");
        var removed = await world.App.Client.PostAsync($"/api/retention/rules/{ruleId}/check", null);
        Assert.True(removed.IsSuccessStatusCode, await removed.Content.ReadAsStringAsync());
        Assert.False(File.Exists(file), "the removal stands though Seerr could not be told");
        var owed = Assert.Single((await world.App.Client.GetFromJsonAsync<JsonElement>("/api/retention/seerr-cleanups")).EnumerateArray());
        Assert.Equal(("Sintel", "movies", "45745", serverId), (owed.GetProperty("title").GetString(), owed.GetProperty("mediaType").GetString(), owed.GetProperty("tmdbId").GetString(), owed.GetProperty("serverId").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(owed.GetProperty("problem").GetString()));
        Assert.True(owed.GetProperty("attempts").GetInt32() >= 1);
        Assert.DoesNotContain(seerr.Server.Requests, request => request.Method == "DELETE");

        // Seerr answers again; the next check of any rule tries the owed clearing at once.
        seerr.Retrieving = true;
        Assert.True((await world.App.Client.PostAsync($"/api/retention/rules/{ruleId}/check", null)).IsSuccessStatusCode);

        Assert.Empty((await world.App.Client.GetFromJsonAsync<JsonElement>("/api/retention/seerr-cleanups")).EnumerateArray());
        Assert.Contains(seerr.Server.Requests, request => request.Method == "DELETE" && request.PathAndQuery == "/api/v1/media/1");
    }

    [Fact]
    public async Task Retention_reads_every_switched_on_Seerr_gets_no_index_when_none_is_connected_and_names_a_server_it_could_not_read_without_letting_it_decide_anything()
    {
        await using var seerr = await Seerr3.StartAsync();
        await using var app = await ApplicationTestHost.StartAsync();
        using var scope = app.Services.CreateScope();
        var retention = scope.ServiceProvider.GetRequiredService<IRetentionSeerr>();

        var none = await retention.ReadAsync(CancellationToken.None);
        Assert.Null(none.Index);
        Assert.Empty(none.Unreachable);

        await ConnectAsync(app, seerr.Server.BaseUrl, "Reachable");
        await ConnectAsync(app, "http://127.0.0.1:1", "Down");
        await ConnectAsync(app, seerr.Server.BaseUrl + "/switched-off", "Off", enabled: false);

        var read = await retention.ReadAsync(CancellationToken.None);

        Assert.NotNull(read.Index);
        Assert.Single(read.Unreachable);
        Assert.NotEmpty(read.Index!.Users);
        var series = read.Index.Find(Show("tt0903747", "1396", null));
        Assert.Single(series!);
        Assert.Equal(("tv", "1396"), (series![0].MediaType, series[0].TmdbId));
        Assert.Contains(seerr.Server.Requests, request => request.PathAndQuery.StartsWith("/api/v1/request", StringComparison.Ordinal));
        Assert.DoesNotContain(seerr.Server.Requests, request => request.PathAndQuery.Contains("switched-off", StringComparison.Ordinal));
    }

    private static RetentionUnit Show(string? imdb, string? tmdb, string? tvdb)
        => new(RetentionLevels.Show, "show-1", null, null, null, "Breaking Bad", "Breaking Bad", 2008, imdb, tmdb, tvdb, null, [], []);

    private static RetentionUnit Film(string? tmdb, string? tvdb)
        => new(RetentionLevels.Movie, "movie-1", null, null, null, "Sintel", "Sintel", 2010, null, tmdb, tvdb, null, [], []);

    [Fact]
    public async Task A_show_is_found_in_Seerrs_requests_by_its_TMDb_id_and_failing_that_its_TVDb_id_a_film_only_by_its_TMDb_id_and_a_title_with_neither_is_not_known_to_Seerr()
    {
        await using var seerr = await Seerr3.StartAsync();
        await using var app = await ApplicationTestHost.StartAsync();
        await ConnectAsync(app, seerr.Server.BaseUrl);
        using var scope = app.Services.CreateScope();
        var index = (await scope.ServiceProvider.GetRequiredService<IRetentionSeerr>().ReadAsync(CancellationToken.None)).Index!;

        Assert.Single(index.Find(Show(null, "1396", null))!);
        Assert.Single(index.Find(Show(null, null, "81189"))!);
        Assert.Single(index.Find(Show(null, "999999", "81189"))!);
        Assert.Empty(index.Find(Show(null, "999999", null))!);
        Assert.Empty(index.Find(Film("1396", "81189"))!);
        Assert.Null(index.Find(Show(null, null, null)));
        Assert.Null(index.Find(Film(null, "81189")));
    }
}
