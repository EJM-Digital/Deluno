using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Filesystem;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// Once a title Seerr asked for has been imported, Deluno tells that Seerr it is available (#852): real files imported into a real library by the
/// real pipeline, the title's Seerr acquisition in the real database, and a Seerr answering over a real loopback port with the replies Seerr 3.5.0
/// itself gave to that call (tests/fixtures/recorded/seerr, #1237: the 200 it gives the JSON body Deluno sends, and the 415 it gives a call without
/// one, used here as a Seerr that refuses the mark).
/// </summary>
public sealed class SeerrIsToldWhatLandedOnRealFilesOnRecordedSeerrTests
{
    private static Recording Seerr(string state) => RecordedReplies.Seerr(state);

    private sealed class World : IAsyncDisposable
    {
        public required RealDiskLibrary Disk { get; init; }

        public required RecordedAppServer SeerrServer { get; init; }

        public required string ConnectionId { get; init; }

        public ApplicationTestHost App => Disk.App;

        public async ValueTask DisposeAsync()
        {
            await Disk.DisposeAsync();
            await SeerrServer.DisposeAsync();
        }

        public async Task<IReadOnlyList<SeerrAcquisitionItem>> AcquisitionsAsync()
            => await App.Services.GetRequiredService<ISeerrAcquisitionsRepository>().ListRecentAsync(ConnectionId, 20, CancellationToken.None);

        public List<RecordedAppServer.Seen> Told => SeerrServer.Requests.Where(request => request.PathAndQuery.EndsWith("/available", StringComparison.Ordinal)).ToList();
    }

    /// <summary>A Seerr that takes the mark (the recorded 200) or refuses it (the recorded 415).</summary>
    private static async Task<World> StartAsync(bool seerrTakesTheMark)
    {
        var seerr = await RecordedAppServer.StartAsync("seerr", [], RecordedAppServer.Answering("POST", "/available", Seerr("media-available").Exchanges[seerrTakesTheMark ? 1 : 0]));
        var app = await ApplicationTestHost.StartAsync(replaceServices: RealDiskLibrary.UseProbe(new ScriptedProbe(_ => ScriptedProbe.Good())));
        Directory.CreateDirectory(Path.Combine(app.DataRoot, "downloads"));
        var disk = await RealDiskLibrary.OpenAsync(app);
        var saved = await app.Client.PostAsJsonAsync("/api/seerr-servers", new { name = "Seerr", baseUrl = seerr.BaseUrl, apiKey = "key-1", isEnabled = true });
        saved.EnsureSuccessStatusCode();
        return new World { Disk = disk, SeerrServer = seerr, ConnectionId = (await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()! };
    }

    private static async Task<string> TvLibraryAsync(World world)
    {
        var root = Directory.CreateDirectory(Path.Combine(world.App.DataRoot, "shows")).FullName;
        var response = await world.App.Client.PostAsJsonAsync("/api/libraries/", new
        {
            name = "Shows", mediaType = "tv", purpose = "collection", rootPath = root, downloadsPath = world.Disk.Downloads,
            importWorkflow = "standard", autoSearchEnabled = false, missingSearchEnabled = false, upgradeSearchEnabled = false
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static async Task<SeriesListItem> SeerrLinkedSeriesAsync(World world, int seerrMediaId)
    {
        var series = world.App.Services.GetRequiredService<ISeriesCatalogRepository>();
        var added = await series.AddAsync(new CreateSeriesRequest("Breaking Bad", 2008, "tt0903747"), CancellationToken.None);
        await series.SyncEpisodeCatalogueAsync(
            added.Id,
            [
                new CatalogueEpisodeItem(1, 1, "Pilot", null, DateTimeOffset.UtcNow.AddYears(-10)),
                new CatalogueEpisodeItem(1, 2, "Cat's in the Bag...", null, DateTimeOffset.UtcNow.AddYears(-10))
            ],
            "provider",
            CancellationToken.None);
        await world.App.Services.GetRequiredService<ISeerrAcquisitionsRepository>()
            .RecordAsync(world.ConnectionId, "Seerr", "tv", added.Id, "Breaking Bad", seerrMediaId, "added", null, "Ben", CancellationToken.None);
        return added;
    }

    private static ImportExecuteRequest EpisodeRequest(string source, string seriesId)
        => new(
            new ImportPreviewRequest(source, Path.GetFileName(source), "tv", "Breaking Bad", 2008, [], [], null, null, SeriesId: seriesId),
            "copy", Overwrite: false, AllowCopyFallback: true);

    [Fact]
    public async Task An_imported_film_Seerr_asked_for_is_reported_available_with_the_JSON_body_Seerr_takes_and_its_acquisition_says_so()
    {
        await using var world = await StartAsync(seerrTakesTheMark: true);
        var movie = await world.Disk.AddMovieAsync("Sintel", 2010);
        await world.App.Services.GetRequiredService<ISeerrAcquisitionsRepository>()
            .RecordAsync(world.ConnectionId, "Seerr", "movies", movie.Id, "Sintel", 1, "added", null, "Ben", CancellationToken.None);
        var source = world.Disk.WriteDownload("Sintel.2010.WEB.1080p.mkv");
        var dispatch = await world.Disk.RecordDispatchAsync(movie.Id, "Sintel 2010 WEB 1080p");

        var result = await world.Disk.ImportAsync(RealDiskLibrary.Request(source, "Sintel", 2010, dispatchId: dispatch));

        Assert.True(result.Succeeded, result.Message);
        var told = Assert.Single(world.Told);
        Assert.Equal(("POST", "/api/v1/media/1/available"), (told.Method, told.PathAndQuery));
        Assert.Equal("{\"is4k\":false}", told.Body);
        Assert.Equal("key-1", told.Header("x-api-key"));
        Assert.Equal("available", Assert.Single(await world.AcquisitionsAsync()).Status);
    }

    [Fact]
    public async Task An_imported_episode_of_a_series_Seerr_asked_for_is_reported_available()
    {
        await using var world = await StartAsync(seerrTakesTheMark: true);
        await TvLibraryAsync(world);
        var series = await SeerrLinkedSeriesAsync(world, seerrMediaId: 2);
        var source = world.Disk.WriteDownload("Breaking.Bad.S01E01.1080p.WEB-DL.mkv");

        var result = await world.Disk.ImportAsync(EpisodeRequest(source, series.Id));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("/api/v1/media/2/available", Assert.Single(world.Told).PathAndQuery);
        Assert.Equal("available", Assert.Single(await world.AcquisitionsAsync()).Status);
    }

    [Fact]
    public async Task A_title_Seerr_never_asked_for_is_not_reported_and_a_Seerr_that_refuses_the_mark_is_kept_on_the_card_not_in_the_import()
    {
        await using var world = await StartAsync(seerrTakesTheMark: false);
        var other = await world.Disk.AddMovieAsync("Elephants Dream", 2006);
        var otherSource = world.Disk.WriteDownload("Elephants.Dream.2006.WEB.1080p.mkv", fill: 0x22);
        Assert.True((await world.Disk.ImportAsync(RealDiskLibrary.Request(otherSource, "Elephants Dream", 2006, dispatchId: await world.Disk.RecordDispatchAsync(other.Id, "Elephants Dream 2006 WEB 1080p")))).Succeeded);
        Assert.Empty(world.Told);

        var movie = await world.Disk.AddMovieAsync("Sintel", 2010);
        await world.App.Services.GetRequiredService<ISeerrAcquisitionsRepository>()
            .RecordAsync(world.ConnectionId, "Seerr", "movies", movie.Id, "Sintel", 1, "added", null, "Ben", CancellationToken.None);
        var source = world.Disk.WriteDownload("Sintel.2010.WEB.1080p.mkv");

        var result = await world.Disk.ImportAsync(RealDiskLibrary.Request(source, "Sintel", 2010, dispatchId: await world.Disk.RecordDispatchAsync(movie.Id, "Sintel 2010 WEB 1080p")));

        Assert.True(result.Succeeded, "an outage at Seerr must never fail an import that already landed");
        Assert.True(File.Exists(result.Response!.Preview.DestinationPath));
        var acquisition = Assert.Single(await world.AcquisitionsAsync());
        Assert.Equal("available-failed", acquisition.Status);
        Assert.False(string.IsNullOrWhiteSpace(acquisition.Message));
    }

    [Fact]
    public async Task An_imported_season_pack_of_a_series_Seerr_asked_for_is_reported_available()
    {
        await using var world = await StartAsync(seerrTakesTheMark: true);
        await TvLibraryAsync(world);
        var series = await SeerrLinkedSeriesAsync(world, seerrMediaId: 2);
        var pack = Directory.CreateDirectory(Path.Combine(world.Disk.Downloads, "Breaking.Bad.S01.1080p.WEB-DL")).FullName;
        foreach (var episode in new[] { 1, 2 })
        {
            var path = Path.Combine(pack, $"Breaking.Bad.S01E{episode:D2}.1080p.WEB-DL.mkv");
            File.WriteAllBytes(path, Enumerable.Repeat((byte)(0x40 + episode), 4096).ToArray());
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));
        }

        var result = await world.Disk.ImportAsync(EpisodeRequest(pack, series.Id));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("/api/v1/media/2/available", Assert.Single(world.Told).PathAndQuery);
        Assert.Equal("available", Assert.Single(await world.AcquisitionsAsync()).Status);
    }

    [Fact]
    public async Task A_report_Seerr_refused_is_made_again_the_next_time_the_title_is_imported()
    {
        await using var world = await StartAsync(seerrTakesTheMark: false);
        var movie = await world.Disk.AddMovieAsync("Sintel", 2010);
        await world.App.Services.GetRequiredService<ISeerrAcquisitionsRepository>()
            .RecordAsync(world.ConnectionId, "Seerr", "movies", movie.Id, "Sintel", 1, "added", null, "Ben", CancellationToken.None);
        var first = world.Disk.WriteDownload("Sintel.2010.WEB.1080p.mkv");
        Assert.True((await world.Disk.ImportAsync(RealDiskLibrary.Request(first, "Sintel", 2010, dispatchId: await world.Disk.RecordDispatchAsync(movie.Id, "Sintel 2010 WEB 1080p")))).Succeeded);
        Assert.Equal("available-failed", Assert.Single(await world.AcquisitionsAsync()).Status);
        await world.SeerrServer.DisposeAsync();

        // The second import is of the same title with a better file; the Seerr that answers now is one that takes the mark.
        await using var back = await RecordedAppServer.StartAsync("seerr", [], RecordedAppServer.Answering("POST", "/available", Seerr("media-available").Exchanges[1]));
        await world.App.Client.PutAsJsonAsync($"/api/seerr-servers/{world.ConnectionId}", new { name = "Seerr", baseUrl = back.BaseUrl, apiKey = "", isEnabled = true });
        var better = world.Disk.WriteDownload("Sintel.2010.Bluray.2160p.mkv", fill: 0x33);
        await world.Disk.ImportAsync(RealDiskLibrary.Request(better, "Sintel", 2010, overwrite: true, forceReplacement: true, dispatchId: await world.Disk.RecordDispatchAsync(movie.Id, "Sintel 2010 Bluray 2160p")));

        Assert.Single(back.Requests, request => request.PathAndQuery.EndsWith("/available", StringComparison.Ordinal));
        Assert.Equal("available", (await world.AcquisitionsAsync()).Single().Status);
    }
}
