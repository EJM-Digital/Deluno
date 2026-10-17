using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Search;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Data;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Quality.Data;
using Deluno.Series.Data;
using Deluno.Worker.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Deluno.Persistence.Tests.Jobs;

/// <summary>
/// Coverage register, area movie-detail: the film page's Defer, Skip once, monitoring shield and "Protect against downgrades" have their
/// effect where it counts. The library's scheduled search is the real handler on the real repositories and SQLite files, and counts the
/// films it asks the indexers about; a hand grab of a worse copy goes through the real route. Nothing about the film is faked.
/// </summary>
public sealed class TheFilmControlsChangeWhatTheSchedulerAndTheGrabDoTests
{
    /// <summary>Counts what the scheduled search asks about and finds nothing, so no download client is ever reached.</summary>
    private class CountingPipeline : DispatchProxy
    {
        public List<string> Asked { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name != nameof(IAcquisitionDecisionPipeline.PlanAsync)) throw new NotSupportedException(targetMethod.Name);
            var request = (AcquisitionDecisionRequest)args![0]!;
            lock (Asked) Asked.Add(request.Title);
            return Task.FromResult(new AcquisitionDecisionPlan(
                new MediaSearchPlan(null, [], "Nothing found."), Deluno.Quality.MediaPolicyCatalog.CurrentVersion, "checked", "Nothing found.", 1, 1, null, false, null, []));
        }
    }

    private sealed record Rig(ApplicationTestHost App, string LibraryId, CountingPipeline Pipeline, FixedTimeProvider Clock, MoviesLibrarySearchJobHandler Handler) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => App.DisposeAsync();

        public async Task<string> AddWantedFilmAsync(string title, int year)
        {
            var added = await App.Client.PostAsJsonAsync("/api/movies/", new { title, releaseYear = year, monitored = true, libraryId = LibraryId, searchForMissingNow = false });
            Assert.True(added.IsSuccessStatusCode, await added.Content.ReadAsStringAsync());
            return (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        }

        public Task RunScheduledSearchAsync()
        {
            var payload = JsonSerializer.Serialize(new
            {
                libraryId = LibraryId, libraryName = "Films", mediaType = "movies", checkMissing = true, checkUpgrades = false,
                maxItems = 10, retryDelayHours = 6, triggeredBy = "schedule"
            });
            return Handler.HandleAsync(new JobQueueItem(
                Guid.NewGuid().ToString("N"), LibrarySearchJobTypes.For("movies"), "test", "running", payload, 0, Clock.GetUtcNow(), Clock.GetUtcNow(),
                null, null, null, null, null, null, null, null, null, 3, null, null), CancellationToken.None);
        }
    }

    private static async Task<Rig> StartAsync()
    {
        var app = await ApplicationTestHost.StartAsync();
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies", "Films", "Films");
        var pipeline = DispatchProxy.Create<IAcquisitionDecisionPipeline, CountingPipeline>();
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var handler = new MoviesLibrarySearchJobHandler(
            app.Services.GetRequiredService<ILibrariesRepository>(),
            app.Services.GetRequiredService<IQualityRepository>(),
            app.Services.GetRequiredService<IJobQueueRepository>(),
            app.Services.GetRequiredService<IMovieCatalogRepository>(),
            app.Services.GetRequiredService<ISeriesCatalogRepository>(),
            pipeline,
            app.Services.GetRequiredService<IDownloadClientGrabService>(),
            app.Services.GetRequiredService<IActivityFeedRepository>(),
            clock);
        return new Rig(app, libraryId, (CountingPipeline)(object)pipeline, clock, handler);
    }

    [Fact]
    public async Task A_wanted_monitored_film_is_asked_about_by_the_scheduled_search()
    {
        await using var rig = await StartAsync();
        await rig.AddWantedFilmAsync("Tenet", 2020);

        await rig.RunScheduledSearchAsync();

        Assert.Equal(["Tenet"], rig.Pipeline.Asked);
    }

    [Fact]
    public async Task A_deferred_film_is_not_searched_until_its_time_is_up_and_then_is()
    {
        await using var rig = await StartAsync();
        var deferred = await rig.AddWantedFilmAsync("Tenet", 2020);
        await rig.AddWantedFilmAsync("Dunkirk", 2017);
        var defer = await rig.App.Client.PostAsJsonAsync($"/api/movies/{deferred}/automation/defer", new { libraryId = rig.LibraryId, hours = 48 });
        Assert.Equal(HttpStatusCode.OK, defer.StatusCode);

        await rig.RunScheduledSearchAsync();
        Assert.Equal(["Dunkirk"], rig.Pipeline.Asked);

        rig.Clock.Advance(TimeSpan.FromHours(49));
        rig.Pipeline.Asked.Clear();
        await rig.RunScheduledSearchAsync();
        Assert.Contains("Tenet", rig.Pipeline.Asked);
    }

    [Fact]
    public async Task Skip_once_passes_over_one_scheduled_search_says_so_in_Activity_and_is_then_used_up()
    {
        await using var rig = await StartAsync();
        var film = await rig.AddWantedFilmAsync("Tenet", 2020);
        var skip = await rig.App.Client.PostAsJsonAsync($"/api/movies/{film}/automation/skip-once", new { libraryId = rig.LibraryId });
        Assert.Equal(HttpStatusCode.OK, skip.StatusCode);
        Assert.Equal(1L, await ApiScenes.ScalarAsync(rig.App, "movies", $"SELECT skip_next_automation_search FROM movie_wanted_state WHERE movie_id = '{film}';"));

        await rig.RunScheduledSearchAsync();

        Assert.Empty(rig.Pipeline.Asked);
        Assert.Equal(0L, await ApiScenes.ScalarAsync(rig.App, "movies", $"SELECT skip_next_automation_search FROM movie_wanted_state WHERE movie_id = '{film}';"));
        Assert.Contains("Skipped one scheduled search", await ApiScenes.TextAsync(rig.App, "movies", $"SELECT last_search_result FROM movie_wanted_state WHERE movie_id = '{film}';"), StringComparison.Ordinal);

        rig.Clock.Advance(TimeSpan.FromHours(7));
        await rig.RunScheduledSearchAsync();
        Assert.Equal(["Tenet"], rig.Pipeline.Asked);
    }

    [Fact]
    public async Task An_unmonitored_film_is_never_searched_for_and_is_again_once_monitored()
    {
        await using var rig = await StartAsync();
        var film = await rig.AddWantedFilmAsync("Tenet", 2020);
        var off = await rig.App.Client.PutAsJsonAsync("/api/movies/monitoring", new { movieIds = new[] { film }, monitored = false });
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);

        await rig.RunScheduledSearchAsync();
        Assert.Empty(rig.Pipeline.Asked);

        await rig.App.Client.PutAsJsonAsync("/api/movies/monitoring", new { movieIds = new[] { film }, monitored = true });
        await rig.RunScheduledSearchAsync();
        Assert.Equal(["Tenet"], rig.Pipeline.Asked);
    }

    private sealed class CountingGrabs : IDownloadClientGrabService
    {
        public int Count { get; private set; }

        public Task<DownloadClientGrabResult> GrabAsync(string clientId, DownloadClientGrabRequest request, CancellationToken cancellationToken)
        {
            Count++;
            return Task.FromResult(new DownloadClientGrabResult(clientId, request.ReleaseName, true, "sent", "Sent to the client.") { ExternalId = $"hash-{Count}" });
        }
    }

    [Fact]
    public async Task A_worse_copy_is_refused_for_a_film_that_is_protected_against_downgrades_and_the_file_stays_put()
    {
        var grabs = new CountingGrabs();
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services =>
        {
            services.RemoveAll<IDownloadClientGrabService>();
            services.AddSingleton<IDownloadClientGrabService>(grabs);
        });
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies", "Films", "Films");
        var connections = app.Services.GetRequiredService<IConnectionsRepository>();
        var client = await connections.CreateDownloadClientAsync(new CreateDownloadClientRequest("Client", "qbittorrent", "localhost", 1234, null, null, null, "movies", "tv", null, 1, true), CancellationToken.None);
        await connections.UpdateDownloadClientHealthAsync(client.Id, "healthy", "Verified.", null, 1, CancellationToken.None);
        Assert.True((await app.Client.PutAsJsonAsync($"/api/libraries/{libraryId}/routing", new { sources = Array.Empty<object>(), downloadClients = new[] { new { downloadClientId = client.Id, priority = 1, category = (string?)null } } })).IsSuccessStatusCode);
        var film = await ApiScenes.FilmOnDiskAsync(app, "Dune", 2021, libraryId, quality: "Bluray 1080p");
        Assert.True((await app.Client.PutAsJsonAsync($"/api/movies/{film.MovieId}/replacement-protection", new { preventLowerQualityReplacements = true })).IsSuccessStatusCode);

        var worse = await app.Client.PostAsJsonAsync($"/api/movies/{film.MovieId}/grab", new
        {
            releaseName = "Dune.2021.720p.HDTV.x264-GRP", downloadUrl = "magnet:?xt=urn:btih:A779FA758F2229586EEBC2E5960EB32FE5659596",
            candidateQuality = "HDTV 720p", sizeBytes = 1_500_000_000L, force = false
        });

        Assert.Equal(HttpStatusCode.BadRequest, worse.StatusCode);
        Assert.Contains("Replacement protection is enabled. HDTV 720p is lower quality than your current file (Bluray 1080p)", await worse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(0, grabs.Count);
        Assert.True(File.Exists(film.FilePath), "a refused downgrade never touches the file");

        // The refusal is the protection's: with it taken off, the same worse copy is only held for a person's say, and sent once they force it.
        Assert.True((await app.Client.PutAsJsonAsync($"/api/movies/{film.MovieId}/replacement-protection", new { preventLowerQualityReplacements = false })).IsSuccessStatusCode);
        object Release(bool force) => new
        {
            releaseName = "Dune.2021.720p.HDTV.x264-GRP", downloadUrl = "magnet:?xt=urn:btih:A779FA758F2229586EEBC2E5960EB32FE5659596",
            candidateQuality = "HDTV 720p", sizeBytes = 1_500_000_000L, force, overrideReason = force ? "You chose this release." : null
        };
        var unprotected = await app.Client.PostAsJsonAsync($"/api/movies/{film.MovieId}/grab", Release(force: false));
        Assert.Equal(HttpStatusCode.BadRequest, unprotected.StatusCode);
        Assert.DoesNotContain("Replacement protection is enabled", await unprotected.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(0, grabs.Count);
        var forced = await app.Client.PostAsJsonAsync($"/api/movies/{film.MovieId}/grab", Release(force: true));
        Assert.True(forced.IsSuccessStatusCode, await forced.Content.ReadAsStringAsync());
        Assert.Equal(1, grabs.Count);
    }
}
