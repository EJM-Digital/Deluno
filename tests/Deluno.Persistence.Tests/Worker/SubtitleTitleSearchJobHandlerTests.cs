using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Integrations.Subtitles;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Media;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Notifications;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Worker.Jobs;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Worker;

/// <summary>
/// #840's user-triggered per-title search — the twin of
/// <see cref="LibrarySubtitleSearchNotificationTests"/>, but reading one
/// title directly rather than a library's bounded, backoff-aware slice.
/// </summary>
public sealed class SubtitleTitleSearchJobHandlerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-28T00:00:00Z");

    [Fact]
    public void Its_job_type_matches_the_lane_that_routes_it()
    {
        Assert.Equal("subtitle.title.search", SubtitleTitleSearchJobHandler.JobType);
    }

    [Fact]
    public async Task Searching_one_title_fetches_only_that_titles_missing_languages()
    {
        var (handler, movies, subtitles, library) = await CreateAsync(new SingleOutcomeFetchService(Found("en")));
        var id = await ImportAsync(movies, library.Id, "Dune");

        var result = await handler.HandleAsync(JobFor(library.Id, id), CancellationToken.None);

        Assert.Contains("Dune", result, StringComparison.Ordinal);
        var held = await subtitles.ListSubtitlesAsync(MediaKind.Movie, id, CancellationToken.None);
        Assert.Contains(held, row => row.Language == "en" && row.Source == "fetched");
    }

    [Fact]
    public async Task A_title_already_holding_everything_asked_for_fetches_nothing()
    {
        var (handler, movies, subtitles, library) = await CreateAsync(new SingleOutcomeFetchService(Found("en")));
        var id = await ImportAsync(movies, library.Id, "Dune");
        await subtitles.RecordFetchedAsync(MediaKind.Movie, id, Row("en"), CancellationToken.None);

        var result = await handler.HandleAsync(JobFor(library.Id, id), CancellationToken.None);

        Assert.Contains("already has every subtitle language", result, StringComparison.Ordinal);
    }

    /// <summary>
    /// The point of reading the title directly (#840): a normal library pass
    /// skips a title still mid-backoff, but a person clicking "search this
    /// now" means now, so it is still searched.
    /// </summary>
    [Fact]
    public async Task A_title_mid_backoff_is_still_searched_because_a_person_asked_now()
    {
        var (handler, movies, subtitles, library) = await CreateAsync(new SingleOutcomeFetchService(Found("en")));
        var id = await ImportAsync(movies, library.Id, "Dune");

        // Just failed, so the ordinary library pass would not offer it again
        // for hours.
        await subtitles.RecordAttemptAsync(
            MediaKind.Movie, id, "en", "Rejected: nothing found", TimeSpan.FromHours(6), CancellationToken.None);

        var result = await handler.HandleAsync(JobFor(library.Id, id), CancellationToken.None);

        Assert.Contains("Dune", result, StringComparison.Ordinal);
        var held = await subtitles.ListSubtitlesAsync(MediaKind.Movie, id, CancellationToken.None);
        Assert.Contains(held, row => row.Language == "en");
    }

    [Fact]
    public async Task Every_attempt_is_recorded_in_the_append_only_history()
    {
        var (handler, movies, subtitles, library) = await CreateAsync(new SingleOutcomeFetchService(Found("en")));
        var id = await ImportAsync(movies, library.Id, "Dune");

        await handler.HandleAsync(JobFor(library.Id, id), CancellationToken.None);

        var history = await subtitles.ListAttemptHistoryAsync(MediaKind.Movie, id, CancellationToken.None);
        Assert.Single(history);
        Assert.Equal("en", history[0].Language);
    }

    private static SubtitleFetchOutcome Found(string language, SubtitleMatch match = SubtitleCutoff.Rung)
        => new(language, Found: true, ProviderKey: "gestdown", WrittenPath: $"x.{language}.srt", HearingImpaired: false, Reason: "Matched.", Match: match);

    private static MediaSubtitleRow Row(string language)
        => new(language, "fetched", Forced: false, HearingImpaired: false, FilePath: "x.srt", StreamIndex: null,
            Codec: "srt", Provider: "gestdown", MatchRung: (int)SubtitleCutoff.Rung);

    private static JobQueueItem JobFor(string libraryId, string mediaId)
        => new(
            Id: "job-1",
            JobType: SubtitleTitleSearchJobHandler.JobType,
            Source: "movies",
            Status: "processing",
            PayloadJson: System.Text.Json.JsonSerializer.Serialize(new
            {
                Kind = MediaKind.Movie,
                LibraryId = libraryId,
                MediaId = mediaId
            }),
            Attempts: 1,
            CreatedUtc: Now,
            ScheduledUtc: Now,
            StartedUtc: Now,
            CompletedUtc: null,
            LeasedUntilUtc: null,
            WorkerId: null,
            LastError: null,
            RelatedEntityType: "movie",
            RelatedEntityId: mediaId,
            IdempotencyKey: null,
            DedupeKey: null,
            MaxAttempts: 3,
            LastAttemptUtc: null,
            NextAttemptUtc: null);

    private static async Task<string> ImportAsync(IMovieCatalogRepository movies, string libraryId, string title)
    {
        await movies.ImportExistingBatchAsync(
            libraryId,
            [
                new ExistingMovieImportRequest(
                    Title: title,
                    ReleaseYear: 2021,
                    WantedStatus: WantedStatuses.Covered,
                    WantedReason: "Imported from your existing library.",
                    CurrentQuality: "WEB 2160p",
                    TargetQuality: "WEB 2160p",
                    QualityCutoffMet: true,
                    FilePath: $@"D:\Media\{title}\{title}.mkv",
                    FileSizeBytes: 8_000_000_000)
            ],
            CancellationToken.None);

        var page = await movies.ListPageAsync(new CatalogueQuery(Search: title), CancellationToken.None);
        return page.Items.Single(item => item.Title == title).Id;
    }

    private static async Task<(
        SubtitleTitleSearchJobHandler Handler,
        SqliteMovieCatalogRepository Movies,
        SqliteMediaSubtitleRepository Subtitles,
        LibraryItem Library)> CreateAsync(ISubtitleFetchService fetchService)
    {
        var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(Now);
        var migrator = new SqliteDatabaseMigrator(storage.Factory, timeProvider);
        await new PlatformSchemaInitializer(storage.Factory, migrator, NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await new MoviesSchemaInitializer(storage.Factory, migrator, NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await new JobsSchemaInitializer(storage.Factory, migrator, NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var libraries = new SqliteLibrariesRepository(storage.Factory, timeProvider);
        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var subtitles = new SqliteMediaSubtitleRepository(storage.Factory, timeProvider);
        var overrides = new SqliteMediaSubtitleOverrideRepository(storage.Factory, timeProvider);
        var jobStore = new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());

        var created = await libraries.CreateLibraryAsync(
            new CreateLibraryRequest(
                Name: "Movies",
                MediaType: "movies",
                Purpose: "Main",
                RootPath: @"D:\Media",
                DownloadsPath: null,
                QualityProfileId: null,
                ImportWorkflow: "standard",
                ProcessorName: null,
                ProcessorOutputPath: null,
                ProcessorTimeoutMinutes: null,
                ProcessorFailureMode: null,
                AutoSearchEnabled: true,
                MissingSearchEnabled: true,
                UpgradeSearchEnabled: true,
                SearchIntervalMinutes: 6,
                RetryDelayHours: 24,
                MaxItemsPerRun: 25),
            CancellationToken.None);
        var library = await libraries.UpdateLibrarySubtitlesAsync(
            created.Id,
            new UpdateLibrarySubtitlesRequest(Languages: ["en"], Mode: "all"),
            CancellationToken.None);

        var notificationService = new InMemoryNotificationService();
        var publisher = new NotificationEventPublisher(notificationService, new NullRealtimeEventPublisher());
        var handler = new SubtitleTitleSearchJobHandler(libraries, subtitles, overrides, fetchService, publisher, jobStore);

        return (handler, movies, subtitles, library!);
    }

    /// <summary>Answers every language the same way, for a single-title test.</summary>
    private sealed class SingleOutcomeFetchService(SubtitleFetchOutcome outcome) : ISubtitleFetchService
    {
        public Task<SubtitleFetchOutcome> FetchAsync(
            SubtitleSearchRequest request,
            string language,
            bool isEpisodeMedia,
            string videoPath,
            string hearingImpairedPreference,
            CancellationToken cancellationToken,
            SubtitleContentModificationPolicy? contentPolicy = null,
            SubtitleNamePolicy? namePolicy = null,
            bool omitLanguageCode = false,
            IReadOnlyCollection<string>? blockedKeys = null)
            => Task.FromResult(outcome);

        public Task<SubtitleFetchOutcome> FetchForcedAsync(
            SubtitleSearchRequest request,
            string language,
            bool isEpisodeMedia,
            string videoPath,
            CancellationToken cancellationToken,
            SubtitleContentModificationPolicy? contentPolicy = null,
            SubtitleNamePolicy? namePolicy = null,
            bool omitLanguageCode = false,
            IReadOnlyCollection<string>? blockedKeys = null)
            => Task.FromResult(new SubtitleFetchOutcome(language, false, null, null, false, "Not used by this test."));

        public Task<IReadOnlyList<SubtitleSearchCandidate>> SearchCandidatesAsync(
            SubtitleSearchRequest request, string language, bool isEpisodeMedia, string? videoPath,
            bool excludeHearingImpaired, CancellationToken cancellationToken,
            SubtitleNamePolicy? namePolicy = null, IReadOnlyCollection<string>? blockedKeys = null)
            => Task.FromResult<IReadOnlyList<SubtitleSearchCandidate>>([]);

        public Task<SubtitleFetchOutcome> DownloadCandidateAsync(
            SubtitleSearchRequest request, string providerKey, string downloadToken, string language,
            bool isEpisodeMedia, string videoPath, CancellationToken cancellationToken,
            SubtitleContentModificationPolicy? contentPolicy = null, SubtitleNamePolicy? namePolicy = null,
            bool omitLanguageCode = false)
            => Task.FromResult(new SubtitleFetchOutcome(language, false, null, null, false, "Not used by this test."));
    }
}
