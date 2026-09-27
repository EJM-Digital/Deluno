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
using Deluno.Notifications.Contracts;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Worker.Jobs;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Worker;

/// <summary>
/// #839: nothing fired anywhere for subtitle activity before this. Covers
/// the three new kinds the library subtitle search fires — a fresh fetch,
/// a fetch that replaces one already held, and a search that finds nothing
/// usable — and that each is suppressed when its own preference is off.
/// </summary>
public sealed class LibrarySubtitleSearchNotificationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-27T00:00:00Z");

    [Fact]
    public async Task A_fresh_fetch_dispatches_subtitle_downloaded()
    {
        var (handler, movies, subtitles, notificationService, library) = await CreateAsync(
            new SingleOutcomeFetchService(Found("en")));
        var movieId = await ImportAsync(movies, library.Id, "Dune");

        await handler.HandleAsync(JobFor(library.Id), CancellationToken.None);

        var notification = Assert.Single(await notificationService.GetNotificationsAsync(50, 0, CancellationToken.None));
        Assert.Equal("subtitle_downloaded", notification.Type);
        Assert.Contains("Dune", notification.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_fetch_replacing_an_already_held_language_dispatches_subtitle_upgraded()
    {
        var (handler, movies, subtitles, notificationService, library) = await CreateAsync(
            new SingleOutcomeFetchService(Found("en", SubtitleMatch.AnyRelease)));
        var movieId = await ImportAsync(movies, library.Id, "Dune");

        // Already held, below the cutoff — exactly why the wanted query still
        // offers it, and why this fetch is a replacement rather than a first.
        await subtitles.RecordFetchedAsync(
            MediaKind.Movie, movieId, Row("en", SubtitleMatch.AnyRelease), CancellationToken.None);

        await handler.HandleAsync(JobFor(library.Id), CancellationToken.None);

        var notification = Assert.Single(await notificationService.GetNotificationsAsync(50, 0, CancellationToken.None));
        Assert.Equal("subtitle_upgraded", notification.Type);
    }

    [Fact]
    public async Task A_search_that_finds_nothing_usable_dispatches_subtitle_failed()
    {
        var (handler, movies, subtitles, notificationService, library) = await CreateAsync(
            new SingleOutcomeFetchService(NotFound("en", "Nothing matched closely enough.")));
        var movieId = await ImportAsync(movies, library.Id, "Dune");

        await handler.HandleAsync(JobFor(library.Id), CancellationToken.None);

        var notification = Assert.Single(await notificationService.GetNotificationsAsync(50, 0, CancellationToken.None));
        Assert.Equal("subtitle_failed", notification.Type);
        Assert.Contains("Nothing matched closely enough.", notification.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disabling_the_preference_suppresses_subtitle_downloaded()
    {
        var (handler, movies, subtitles, notificationService, library) = await CreateAsync(
            new SingleOutcomeFetchService(Found("en")));
        await ImportAsync(movies, library.Id, "Dune");

        var preferences = await notificationService.GetPreferencesAsync(CancellationToken.None);
        preferences.SubtitleDownloadedEnabled = false;
        await notificationService.UpdatePreferencesAsync(preferences, CancellationToken.None);

        await handler.HandleAsync(JobFor(library.Id), CancellationToken.None);

        Assert.Empty(await notificationService.GetNotificationsAsync(50, 0, CancellationToken.None));
    }

    private static SubtitleFetchOutcome Found(string language, SubtitleMatch match = SubtitleCutoff.Rung)
        => new(language, Found: true, ProviderKey: "gestdown", WrittenPath: $"x.{language}.srt", HearingImpaired: false, Reason: "Matched.", Match: match);

    private static SubtitleFetchOutcome NotFound(string language, string reason)
        => new(language, Found: false, ProviderKey: null, WrittenPath: null, HearingImpaired: false, Reason: reason);

    private static MediaSubtitleRow Row(string language, SubtitleMatch match)
        => new(language, "fetched", Forced: false, HearingImpaired: false, FilePath: "x.srt", StreamIndex: null,
            Codec: "srt", Provider: "gestdown", MatchRung: (int)match);

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
        LibrarySubtitleSearchJobHandler Handler,
        SqliteMovieCatalogRepository Movies,
        SqliteMediaSubtitleRepository Subtitles,
        InMemoryNotificationService NotificationService,
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
                SearchIntervalHours: 6,
                RetryDelayHours: 24,
                MaxItemsPerRun: 25),
            CancellationToken.None);
        var library = await libraries.UpdateLibrarySubtitlesAsync(
            created.Id,
            new UpdateLibrarySubtitlesRequest(Languages: ["en"], Mode: "all"),
            CancellationToken.None);

        var notificationService = new InMemoryNotificationService();
        var publisher = new NotificationEventPublisher(notificationService, new NullRealtimeEventPublisher());
        var handler = new LibrarySubtitleSearchJobHandler(libraries, subtitles, fetchService, jobStore, publisher);

        return (handler, movies, subtitles, notificationService, library!);
    }

    /// <summary>Answers every language the same way, for a single-title test.</summary>
    private sealed class SingleOutcomeFetchService(SubtitleFetchOutcome outcome) : ISubtitleFetchService
    {
        public Task<SubtitleFetchOutcome> FetchAsync(
            SubtitleSearchRequest request,
            string language,
            bool isEpisodeMedia,
            string videoPath,
            bool excludeHearingImpaired,
            CancellationToken cancellationToken,
            SubtitleContentModificationPolicy? contentPolicy = null,
            SubtitleNamePolicy? namePolicy = null,
            bool omitLanguageCode = false)
            => Task.FromResult(outcome);
    }
}
