using System.Net;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>Live's one shelf (#1103): each arrival names its library, and every library has its own counts.</summary>
public sealed class LiveShelfLibraryTests
{
    private static CreateLibraryRequest NewLibrary(string name, string mediaType, string root)
        => new(
            Name: name, MediaType: mediaType, Purpose: "General", RootPath: root, DownloadsPath: null, QualityProfileId: null,
            ImportWorkflow: null, ProcessorName: null, ProcessorOutputPath: null, ProcessorTimeoutMinutes: null, ProcessorFailureMode: null,
            AutoSearchEnabled: false, MissingSearchEnabled: false, UpgradeSearchEnabled: false, SearchIntervalMinutes: null,
            RetryDelayHours: null, MaxItemsPerRun: null);

    private static async Task ExecuteAsync(IDelunoDatabaseConnectionFactory databases, string database, string sql)
    {
        await using var connection = await databases.OpenConnectionAsync(database);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<JsonElement> GetAsync(ApplicationTestHost app, string url)
    {
        var response = await app.Client.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private sealed record Scene(string HdId, string KidsId, string FirstMovie, string SecondMovie);

    private static async Task<Scene> SeedAsync(ApplicationTestHost app)
    {
        var libraries = app.Services.GetRequiredService<ILibrariesRepository>();
        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();
        var databases = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        var hd = await libraries.CreateLibraryAsync(NewLibrary("Movies HD", "movies", "/data/hd"), CancellationToken.None);
        var kids = await libraries.CreateLibraryAsync(NewLibrary("Kids", "movies", "/data/kids"), CancellationToken.None);

        var first = await movies.AddAsync(new CreateMovieRequest("Sintel", 2010, null, true, hd.Id), CancellationToken.None);
        await movies.EnsureWantedStateAsync(first.Id, hd.Id, WantedStatuses.Covered, "t", true, "WEBDL-1080p", "WEBDL-1080p", true, CancellationToken.None);
        var second = await movies.AddAsync(new CreateMovieRequest("Moon", 2009, null, true, kids.Id), CancellationToken.None);
        await movies.EnsureWantedStateAsync(second.Id, kids.Id, WantedStatuses.Upgrade, "t", true, "WEBDL-720p", "WEBDL-1080p", false, CancellationToken.None);
        var third = await movies.AddAsync(new CreateMovieRequest("Elephants", 2006, null, true, kids.Id), CancellationToken.None);
        await movies.EnsureWantedStateAsync(third.Id, kids.Id, WantedStatuses.Missing, "t", false, null, "WEBDL-1080p", false, CancellationToken.None);

        var now = DateTimeOffset.UtcNow.ToString("O");
        // The first import records its library; the second is an older event that does not, so the title's own row names it.
        await ExecuteAsync(databases, DelunoDatabaseNames.Jobs,
            $$"""
            INSERT INTO activity_events (id, category, message, details_json, related_entity_type, related_entity_id, created_utc)
            VALUES ('a1', 'filesystem.import.completed', 'Imported Sintel', '{"ReleaseNameEvidence":"Sintel.2010.1080p","NewQuality":"WEBDL 1080p","LibraryId":"{{hd.Id}}"}', 'movie', '{{first.Id}}', '{{now}}'),
                   ('a2', 'filesystem.import.completed', 'Imported Moon', '{"ReleaseNameEvidence":"Moon.2009.720p","NewQuality":"WEBDL 720p"}', 'movie', '{{second.Id}}', '{{now}}');
            """);
        return new Scene(hd.Id, kids.Id, first.Id, second.Id);
    }

    [Fact]
    public async Task Each_arrival_carries_the_library_it_landed_in()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SeedAsync(app);
        var since = Uri.EscapeDataString(DateTimeOffset.UtcNow.Date.ToString("O"));

        var live = await GetAsync(app, $"/api/dashboard/live-extras?sinceUtc={since}");

        var arrivals = live.GetProperty("arrivals").EnumerateArray().ToArray();
        var sintel = arrivals.Single(item => item.GetProperty("entityId").GetString() == scene.FirstMovie);
        Assert.Equal(scene.HdId, sintel.GetProperty("libraryId").GetString());
        Assert.Equal("Movies HD", sintel.GetProperty("libraryName").GetString());
        var moon = arrivals.Single(item => item.GetProperty("entityId").GetString() == scene.SecondMovie);
        Assert.Equal(scene.KidsId, moon.GetProperty("libraryId").GetString());
        Assert.Equal("Kids", moon.GetProperty("libraryName").GetString());
    }

    [Fact]
    public async Task The_summary_counts_every_library_the_way_its_own_page_does()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SeedAsync(app);

        var summary = await GetAsync(app, "/api/dashboard/summary");

        var counts = summary.GetProperty("library").GetProperty("libraryCounts").EnumerateArray().ToArray();
        Assert.Equal(2, counts.Length);
        var hd = counts.Single(item => item.GetProperty("libraryId").GetString() == scene.HdId);
        Assert.Equal("Movies HD", hd.GetProperty("libraryName").GetString());
        Assert.Equal("movies", hd.GetProperty("mediaType").GetString());
        Assert.Equal(1, hd.GetProperty("met").GetInt32());
        Assert.Equal(0, hd.GetProperty("upgradable").GetInt32());
        Assert.Equal(0, hd.GetProperty("missing").GetInt32());
        Assert.Equal(1, hd.GetProperty("arrivedToday").GetInt32());
        var kids = counts.Single(item => item.GetProperty("libraryId").GetString() == scene.KidsId);
        Assert.Equal(0, kids.GetProperty("met").GetInt32());
        Assert.Equal(1, kids.GetProperty("upgradable").GetInt32());
        Assert.Equal(1, kids.GetProperty("missing").GetInt32());
        Assert.Equal(1, kids.GetProperty("arrivedToday").GetInt32());
    }
}
