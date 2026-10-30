using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
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
using Deluno.Worker.Dashboard;
using Deluno.Worker.Jobs;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The Live board's Searching lane (#1442): every title Deluno is asking the indexers about right now is in the Live
/// extras while its search runs (a search a person started, a title just added, a cycle's pass over a library) and is
/// gone when the search ends, whether it found nothing or not. A title that is only waiting for its turn is never in it.
/// The handler is the real one on the real repositories and files of the real host; only the indexers are held and answer
/// "nothing found".
/// </summary>
public sealed class TheSearchingLaneListsEveryTitleBeingSearchedForTests
{
    /// <summary>An acquisition pipeline that waits inside the search until the test lets it go.</summary>
    private class HoldingPipeline : DispatchProxy
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Asked { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name != nameof(IAcquisitionDecisionPipeline.PlanAsync)) throw new NotSupportedException(targetMethod.Name);
            var request = (AcquisitionDecisionRequest)args![0]!;
            lock (Asked) Asked.Add(request.Title);
            return Wait();
        }

        private async Task<AcquisitionDecisionPlan> Wait()
        {
            Reached.TrySetResult();
            await Release.Task;
            return new AcquisitionDecisionPlan(
                new MediaSearchPlan(null, [], "Nothing found."), Deluno.Quality.MediaPolicyCatalog.CurrentVersion, "checked", "Nothing found.", 1, 1, null, false, null, []);
        }
    }

    private sealed record Rig(ApplicationTestHost App, string LibraryId, HoldingPipeline Pipeline, MoviesLibrarySearchJobHandler Handler) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => App.DisposeAsync();

        public Task RunAsync(string triggeredBy, string? targetEntityId)
        {
            var payload = JsonSerializer.Serialize(new
            {
                libraryId = LibraryId, libraryName = "Films", mediaType = "movies", checkMissing = true, checkUpgrades = false,
                maxItems = 10, retryDelayHours = 6, triggeredBy, targetEntityId
            });
            var now = DateTimeOffset.UtcNow;
            return Task.Run(() => Handler.HandleAsync(new JobQueueItem(
                Guid.NewGuid().ToString("N"), LibrarySearchJobTypes.For("movies"), "test", "running", payload, 0, now, now,
                null, null, null, null, null, null, null, null, null, 3, null, null), CancellationToken.None));
        }

        public async Task<JsonElement> SearchingAsync()
        {
            var live = await App.Client.GetFromJsonAsync<JsonElement>($"/api/dashboard/live-extras?sinceUtc={Uri.EscapeDataString(DateTimeOffset.UtcNow.Date.ToString("O"))}");
            return live.GetProperty("searching");
        }
    }

    private static async Task<Rig> StartAsync(IRealtimeEventPublisher? realtime = null)
    {
        var app = await ApplicationTestHost.StartAsync();
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies", "Films", "Films");
        var pipeline = DispatchProxy.Create<IAcquisitionDecisionPipeline, HoldingPipeline>();
        var handler = new MoviesLibrarySearchJobHandler(
            app.Services.GetRequiredService<ILibrariesRepository>(),
            app.Services.GetRequiredService<IQualityRepository>(),
            app.Services.GetRequiredService<IJobQueueRepository>(),
            app.Services.GetRequiredService<IMovieCatalogRepository>(),
            app.Services.GetRequiredService<ISeriesCatalogRepository>(),
            pipeline,
            app.Services.GetRequiredService<IDownloadClientGrabService>(),
            app.Services.GetRequiredService<IActivityFeedRepository>(),
            TimeProvider.System,
            searchesInFlight: realtime is null ? app.Services.GetRequiredService<SearchesInFlight>() : new SearchesInFlight(TimeProvider.System, realtime));
        return new Rig(app, libraryId, (HoldingPipeline)(object)pipeline, handler);
    }

    private static async Task<string> AddFilmAsync(Rig rig, string title, int year)
    {
        var added = await rig.App.Client.PostAsJsonAsync("/api/movies/", new { title, releaseYear = year, monitored = true, libraryId = rig.LibraryId, searchForMissingNow = false });
        Assert.True(added.IsSuccessStatusCode, await added.Content.ReadAsStringAsync());
        return (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Nothing_is_listed_when_nothing_is_being_searched_for()
    {
        await using var rig = await StartAsync();
        await AddFilmAsync(rig, "Tenet", 2020);

        Assert.Equal(0, (await rig.SearchingAsync()).GetArrayLength());
    }

    [Theory]
    [InlineData("manual", "manual")]
    [InlineData("movie-added", "manual")]
    public async Task A_search_a_person_started_or_a_title_just_added_is_listed_while_it_runs_and_gone_when_it_ends_with_nothing(string triggeredBy, string startedBy)
    {
        await using var rig = await StartAsync();
        var id = await AddFilmAsync(rig, "Tenet", 2020);

        var run = rig.RunAsync(triggeredBy, id);
        await rig.Pipeline.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var searching = await rig.SearchingAsync();
        var item = Assert.Single(searching.EnumerateArray());
        Assert.Equal("Tenet", item.GetProperty("title").GetString());
        Assert.Equal("movie", item.GetProperty("entityType").GetString());
        Assert.Equal(id, item.GetProperty("entityId").GetString());
        Assert.Equal(startedBy, item.GetProperty("startedBy").GetString());
        Assert.StartsWith("Looking for it", item.GetProperty("label").GetString());

        rig.Pipeline.Release.SetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(0, (await rig.SearchingAsync()).GetArrayLength());
    }

    [Fact]
    public async Task A_cycle_lists_the_title_it_is_on_and_not_the_wanted_titles_waiting_for_their_turn()
    {
        await using var rig = await StartAsync();
        await AddFilmAsync(rig, "Tenet", 2020);
        await AddFilmAsync(rig, "Dunkirk", 2017);

        var run = rig.RunAsync("schedule", null);
        await rig.Pipeline.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var searching = await rig.SearchingAsync();
        var item = Assert.Single(searching.EnumerateArray());
        var onNow = Assert.Single(rig.Pipeline.Asked);
        Assert.Equal(onNow, item.GetProperty("title").GetString());
        Assert.Equal("missing", item.GetProperty("startedBy").GetString());

        rig.Pipeline.Release.SetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(0, (await rig.SearchingAsync()).GetArrayLength());
    }

    [Fact]
    public async Task Each_change_is_pushed_with_the_whole_list_so_no_screen_has_to_ask()
    {
        var realtime = new RecordingRealtimeEventPublisher();
        await using var rig = await StartAsync(realtime);
        var id = await AddFilmAsync(rig, "Tenet", 2020);

        var run = rig.RunAsync("manual", id);
        await rig.Pipeline.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var started = Assert.Single(realtime.LiveEventsOf<SearchesInFlightChangedEvent>(LiveEventNames.SearchesInFlightChanged));
        Assert.Equal("Tenet", Assert.Single(started.Searches).Title);

        rig.Pipeline.Release.SetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(30));
        var pushed = realtime.LiveEventsOf<SearchesInFlightChangedEvent>(LiveEventNames.SearchesInFlightChanged);
        Assert.Equal(2, pushed.Count);
        Assert.Empty(pushed[^1].Searches);
    }

    [Theory]
    [InlineData(0, "Looking for it")]
    [InlineData(1, "Checking 1 indexer")]
    [InlineData(5, "Checking 5 indexers")]
    public void The_label_says_how_many_indexers_are_being_asked(int indexers, string expected)
        => Assert.Equal(expected, SearchesInFlight.CheckingLabel(indexers));
}
