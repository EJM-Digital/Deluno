using System.Reflection;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Integrations.Subtitles;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Media;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Worker.Jobs;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Worker;

/// <summary>
/// The subtitle search job on real database files: a library that asks for no languages, one that is gone and one with
/// nothing missing each say so and ask no provider; a slice is as long as the library's items-per-run; a library with more
/// to do queues its next slice once; a provider that is not set up ends the slice with the way to fix it; and a subtitle
/// found for the file is recorded as fetched and stops being wanted.
/// </summary>
public sealed class LibrarySubtitleSearchSlicesTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-18T00:00:00Z");

    [Fact]
    public async Task A_library_asking_for_no_subtitle_languages_says_so_and_asks_no_provider()
    {
        var world = await StartAsync(languages: [], titles: 1);

        var message = await world.Handler.HandleAsync(JobFor(world.Library.Id), CancellationToken.None);

        Assert.Equal("Movies is not asking for any subtitle languages, so nothing was searched for.", message);
        Assert.Equal(0, world.Fetch.Asked);
    }

    [Fact]
    public async Task A_library_that_no_longer_exists_says_so_and_asks_no_provider()
    {
        var world = await StartAsync(languages: ["en"], titles: 1);

        var message = await world.Handler.HandleAsync(JobFor("library-that-was-removed"), CancellationToken.None);

        Assert.Equal("That library no longer exists, so nothing was searched for.", message);
        Assert.Equal(0, world.Fetch.Asked);
    }

    [Fact]
    public async Task A_library_whose_files_all_hold_the_asked_for_subtitles_says_so_and_queues_nothing()
    {
        var world = await StartAsync(languages: ["en"], titles: 0);

        var message = await world.Handler.HandleAsync(JobFor(world.Library.Id), CancellationToken.None);

        Assert.Equal("Every file in Movies has the subtitles you asked for.", message);
        Assert.Equal(0, world.Fetch.Asked);
        Assert.DoesNotContain(await world.Jobs.ListAsync(50, CancellationToken.None), job => job.JobType == "library.subtitles.search");
    }

    [Fact]
    public async Task A_slice_is_as_long_as_the_librarys_items_per_run_and_a_library_with_more_to_do_queues_the_next_slice_once()
    {
        var world = await StartAsync(languages: ["en"], titles: 3, maxItemsPerRun: 2);

        await world.Handler.HandleAsync(JobFor(world.Library.Id), CancellationToken.None);

        Assert.Equal(2, world.Fetch.Asked);
        var next = Assert.Single(await world.Jobs.ListAsync(50, CancellationToken.None), job => job.JobType == "library.subtitles.search");
        Assert.Equal($"library.subtitles.search:{world.Library.Id}", next.DedupeKey);
        Assert.Equal(world.Library.Id, next.RelatedEntityId);

        // The two asked about wait out their retry delay, so the next slice reaches the third title instead of asking the same two again.
        await world.Handler.HandleAsync(JobFor(world.Library.Id), CancellationToken.None);
        Assert.Equal(3, world.Fetch.Asked);
    }

    [Fact]
    public async Task A_provider_that_is_not_set_up_ends_the_slice_with_the_way_to_fix_it_and_queues_no_next_slice()
    {
        var world = await StartAsync(languages: ["en"], titles: 3, maxItemsPerRun: 2, answer: _ => Missing("No subtitle providers are set up."));

        var message = await world.Handler.HandleAsync(JobFor(world.Library.Id), CancellationToken.None);

        Assert.Equal("Movies wants subtitles and no providers are set up yet, so nothing could be fetched. Add one under Find & Download.", message);
        Assert.Equal(1, world.Fetch.Asked);
        Assert.DoesNotContain(await world.Jobs.ListAsync(50, CancellationToken.None), job => job.JobType == "library.subtitles.search");
    }

    [Fact]
    public async Task A_subtitle_made_for_the_file_is_recorded_as_fetched_and_the_title_stops_being_wanted()
    {
        var world = await StartAsync(languages: ["en"], titles: 1, answer: _ => new SubtitleFetchOutcome(
            "en", Found: true, ProviderKey: "gestdown", WrittenPath: "Dune.2021.en.srt", HearingImpaired: false, Reason: "Matched.",
            Match: SubtitleCutoff.Rung, SourceKey: "gestdown|good-1"));

        await world.Handler.HandleAsync(JobFor(world.Library.Id), CancellationToken.None);

        var held = Assert.Single(await world.Subtitles.ListSubtitlesAsync(MediaKind.Movie, world.MovieIds[0], CancellationToken.None));
        Assert.Equal("en", held.Language);
        Assert.Equal("fetched", held.Source);
        Assert.False(held.Forced);
        Assert.Equal("Dune.2021.en.srt", held.FilePath);
        Assert.Empty(await world.Subtitles.ListWantedAsync(MediaKind.Movie, world.Library.Id, ["en"], 10, false, false, CancellationToken.None));
        Assert.DoesNotContain(await world.Jobs.ListAsync(50, CancellationToken.None), job => job.JobType == "subtitle.sync");
    }

    [Fact]
    public async Task A_subtitle_that_is_not_provably_in_time_is_kept_and_queued_to_be_timed_once()
    {
        var world = await StartAsync(languages: ["en"], titles: 1, answer: _ => new SubtitleFetchOutcome(
            "en", Found: true, ProviderKey: "gestdown", WrittenPath: "Dune.2021.en.srt", HearingImpaired: false, Reason: "Matched.",
            Match: SubtitleMatch.AnyRelease, SourceKey: "gestdown|any-1"));

        await world.Handler.HandleAsync(JobFor(world.Library.Id), CancellationToken.None);

        Assert.Single(await world.Subtitles.ListSubtitlesAsync(MediaKind.Movie, world.MovieIds[0], CancellationToken.None));
        var sync = Assert.Single(await world.Jobs.ListAsync(50, CancellationToken.None), job => job.JobType == "subtitle.sync");
        Assert.Equal("subtitle.sync:Dune.2021.en.srt", sync.DedupeKey);
    }

    private static SubtitleFetchOutcome Missing(string reason)
        => new("en", Found: false, ProviderKey: null, WrittenPath: null, HearingImpaired: false, Reason: reason);

    private static JobQueueItem JobFor(string libraryId)
        => new(
            Id: "job-1",
            JobType: "library.subtitles.search",
            Source: "movies",
            Status: "processing",
            PayloadJson: System.Text.Json.JsonSerializer.Serialize(new { LibraryId = libraryId }),
            Attempts: 1,
            CreatedUtc: Now,
            ScheduledUtc: Now,
            StartedUtc: Now,
            CompletedUtc: null,
            LeasedUntilUtc: null,
            WorkerId: null,
            LastError: null,
            RelatedEntityType: "library",
            RelatedEntityId: libraryId,
            IdempotencyKey: null,
            DedupeKey: null,
            MaxAttempts: 3,
            LastAttemptUtc: null,
            NextAttemptUtc: null);

    private sealed record World(
        LibrarySubtitleSearchJobHandler Handler,
        SqliteMediaSubtitleRepository Subtitles,
        SqliteJobStore Jobs,
        LibraryItem Library,
        IReadOnlyList<string> MovieIds,
        Fetch Fetch);

    private static async Task<World> StartAsync(
        string[] languages,
        int titles,
        int maxItemsPerRun = 25,
        Func<int, SubtitleFetchOutcome>? answer = null)
    {
        var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Now);
        var migrator = new SqliteDatabaseMigrator(storage.Factory, clock);
        await new PlatformSchemaInitializer(storage.Factory, migrator, NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await new MoviesSchemaInitializer(storage.Factory, migrator, NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await new JobsSchemaInitializer(storage.Factory, migrator, NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var libraries = new SqliteLibrariesRepository(storage.Factory, clock);
        var movies = new SqliteMovieCatalogRepository(storage.Factory, clock);
        var subtitles = new SqliteMediaSubtitleRepository(storage.Factory, clock);
        var jobs = new SqliteJobStore(storage.Factory, clock, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());
        var created = await libraries.CreateLibraryAsync(
            new CreateLibraryRequest(
                Name: "Movies", MediaType: "movies", Purpose: "Main", RootPath: @"D:\Media", DownloadsPath: null, QualityProfileId: null,
                ImportWorkflow: "standard", ProcessorName: null, ProcessorOutputPath: null, ProcessorTimeoutMinutes: null,
                ProcessorFailureMode: null, AutoSearchEnabled: true, MissingSearchEnabled: true, UpgradeSearchEnabled: true,
                SearchIntervalMinutes: 6, RetryDelayHours: 24, MaxItemsPerRun: maxItemsPerRun),
            CancellationToken.None);
        var library = (await libraries.UpdateLibrarySubtitlesAsync(created.Id, new UpdateLibrarySubtitlesRequest(Languages: languages, Mode: "all"), CancellationToken.None))!;

        var names = new[] { "Dune", "Arrival", "Sicario", "Prisoners", "Enemy" }.Take(titles).ToArray();
        await movies.ImportExistingBatchAsync(
            library.Id,
            names.Select(title => new ExistingMovieImportRequest(
                Title: title, ReleaseYear: 2021, WantedStatus: WantedStatuses.Covered, WantedReason: "Imported from your existing library.",
                CurrentQuality: "WEB 2160p", TargetQuality: "WEB 2160p", QualityCutoffMet: true,
                FilePath: $@"D:\Media\{title}\{title}.mkv", FileSizeBytes: 8_000_000_000)).ToArray(),
            CancellationToken.None);
        var ids = (await movies.ListPageAsync(new CatalogueQuery(), CancellationToken.None)).Items.OrderBy(item => Array.IndexOf(names, item.Title)).Select(item => item.Id).ToList();

        var fetch = Fetch.Create(answer ?? (_ => Missing("No provider had a usable subtitle for this one.")));
        var handler = new LibrarySubtitleSearchJobHandler(libraries, subtitles, fetch.Proxy, jobs, jobs);
        return new World(handler, subtitles, jobs, library, ids, fetch);
    }

    private class Fetch : DispatchProxy
    {
        private int _asked;
        private Func<int, SubtitleFetchOutcome> _answer = _ => throw new InvalidOperationException();

        public int Asked => Volatile.Read(ref _asked);

        public ISubtitleFetchService Proxy => (ISubtitleFetchService)(object)this;

        public static Fetch Create(Func<int, SubtitleFetchOutcome> answer)
        {
            var fetch = (Fetch)(object)DispatchProxy.Create<ISubtitleFetchService, Fetch>();
            fetch._answer = answer;
            return fetch;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case nameof(ISubtitleFetchService.FetchAsync):
                    return Task.FromResult(_answer(Interlocked.Increment(ref _asked)));
                case nameof(ISubtitleFetchService.FetchForcedAsync):
                    return Task.FromResult(new SubtitleFetchOutcome("en", Found: false, ProviderKey: null, WrittenPath: null, HearingImpaired: false, Reason: "Not asked for in this test."));
                default:
                    throw new NotSupportedException(targetMethod?.Name);
            }
        }
    }
}
