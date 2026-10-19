using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
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

namespace Deluno.Persistence.Tests.Jobs;

/// <summary>
/// The library's retry delay is honoured by its scheduled search: a title that found nothing is not asked about again until the
/// delay has passed, and is then. The scheduled search is the real handler on the real repositories and SQLite files; only the
/// indexers are counted and answer "nothing found".
/// </summary>
public sealed class ATitleThatFoundNothingWaitsForTheRetryDelayTests
{
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

        public Task RunScheduledSearchAsync(int retryDelayHours)
        {
            var payload = JsonSerializer.Serialize(new
            {
                libraryId = LibraryId, libraryName = "Films", mediaType = "movies", checkMissing = true, checkUpgrades = false,
                maxItems = 10, retryDelayHours, triggeredBy = "schedule"
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

    private static async Task AddWantedFilmAsync(Rig rig, string title, int year)
    {
        var added = await rig.App.Client.PostAsJsonAsync("/api/movies/", new { title, releaseYear = year, monitored = true, libraryId = rig.LibraryId, searchForMissingNow = false });
        Assert.True(added.IsSuccessStatusCode, await added.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_title_that_found_nothing_is_not_asked_about_again_until_the_retry_delay_has_passed_and_then_is()
    {
        await using var rig = await StartAsync();
        await AddWantedFilmAsync(rig, "Tenet", 2020);

        await rig.RunScheduledSearchAsync(retryDelayHours: 6);
        Assert.Equal(["Tenet"], rig.Pipeline.Asked);

        rig.Clock.Advance(TimeSpan.FromHours(5));
        rig.Pipeline.Asked.Clear();
        await rig.RunScheduledSearchAsync(retryDelayHours: 6);
        Assert.Empty(rig.Pipeline.Asked);

        rig.Clock.Advance(TimeSpan.FromHours(2));
        await rig.RunScheduledSearchAsync(retryDelayHours: 6);
        Assert.Equal(["Tenet"], rig.Pipeline.Asked);
    }

    [Fact]
    public async Task The_wait_is_the_delay_the_library_asks_for_so_a_longer_one_holds_the_title_back_longer()
    {
        await using var rig = await StartAsync();
        await AddWantedFilmAsync(rig, "Tenet", 2020);

        await rig.RunScheduledSearchAsync(retryDelayHours: 24);
        rig.Clock.Advance(TimeSpan.FromHours(7));
        rig.Pipeline.Asked.Clear();
        await rig.RunScheduledSearchAsync(retryDelayHours: 24);
        Assert.Empty(rig.Pipeline.Asked);

        var held = (await rig.App.Client.GetFromJsonAsync<JsonElement>($"/api/search-retry-windows?libraryId={rig.LibraryId}")).GetProperty("items").EnumerateArray().Single();
        Assert.Equal("missing", held.GetProperty("actionKind").GetString());
        Assert.True(held.GetProperty("nextEligibleUtc").GetDateTimeOffset() > rig.Clock.GetUtcNow().AddHours(16), "the screen's 'held back until' time is a day after the first try");

        rig.Clock.Advance(TimeSpan.FromHours(18));
        await rig.RunScheduledSearchAsync(retryDelayHours: 24);
        Assert.Equal(["Tenet"], rig.Pipeline.Asked);
    }
}
