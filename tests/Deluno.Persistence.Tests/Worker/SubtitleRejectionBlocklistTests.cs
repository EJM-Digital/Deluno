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
/// #898: a corrupt, wrong-encoding or wrong-language subtitle candidate must
/// be rejected, remembered so it is never offered again, and named in
/// Activity — not just counted in the job summary.
/// </summary>
public sealed class SubtitleRejectionBlocklistTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-27T00:00:00Z");

    [Fact]
    public async Task A_rejected_candidate_is_blocklisted_and_named_in_activity()
    {
        var (handler, movies, subtitles, jobStore, library, _) = await CreateAsync(
            new QueueFetchService(
                Rejected("en", "gestdown", "gestdown|bad-1", "Dune.2021.WEB", "This is a captcha page, not a subtitle.")));
        var movieId = await ImportAsync(movies, library.Id, "Dune");

        await handler.HandleAsync(JobFor(library.Id), CancellationToken.None);

        var blocked = await subtitles.ListBlockedAsync(MediaKind.Movie, movieId, CancellationToken.None);
        var entry = Assert.Single(blocked);
        Assert.Equal("gestdown|bad-1", entry.BlockKey);
        Assert.Equal("gestdown", entry.ProviderKey);
        Assert.Equal("This is a captcha page, not a subtitle.", entry.Reason);

        var activity = await jobStore.ListActivityAsync(50, "movie", movieId, CancellationToken.None);
        var recorded = Assert.Single(activity);
        Assert.Equal("subtitle.rejected", recorded.Category);
        Assert.Contains("Dune", recorded.Message, StringComparison.Ordinal);
        Assert.Contains("captcha page", recorded.Message, StringComparison.Ordinal);
        Assert.Contains("not be fetched again", recorded.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_next_search_never_offers_the_same_rejected_candidate_again()
    {
        var fetchService = new QueueFetchService(
            Rejected("en", "gestdown", "gestdown|bad-1", "Dune.2021.WEB", "Corrupt download."),
            Found("en", "gestdown|good-2"));
        var (handler, movies, _, _, library, timeProvider) = await CreateAsync(fetchService);
        await ImportAsync(movies, library.Id, "Dune");

        // First pass: the bad candidate is offered, rejected, and blocklisted.
        await handler.HandleAsync(JobFor(library.Id), CancellationToken.None);
        Assert.DoesNotContain("gestdown|bad-1", fetchService.BlockedKeysSeenByCall[0]);

        // A rejected attempt is remembered with a backoff, so the search would
        // not revisit this title at all until it elapses (#898's own memory,
        // not the blocklist). Advance past it to reach the second search.
        timeProvider.Advance(TimeSpan.FromHours(7));

        // Second pass: the search must hand the fetch service the now-blocked
        // key, so a provider search never re-offers the same bad file.
        await handler.HandleAsync(JobFor(library.Id), CancellationToken.None);
        Assert.Contains("gestdown|bad-1", fetchService.BlockedKeysSeenByCall[1]);
    }

    private static SubtitleFetchOutcome Rejected(
        string language, string providerKey, string blockKey, string releaseName, string reason)
        => new(
            language, Found: false, ProviderKey: null, WrittenPath: null, HearingImpaired: false,
            Reason: "No provider had a usable subtitle for this one.",
            RejectedCandidates: [new RejectedSubtitleCandidate(blockKey, language, providerKey, releaseName, reason)]);

    private static SubtitleFetchOutcome Found(string language, string sourceKey)
        => new(
            language, Found: true, ProviderKey: "gestdown", WrittenPath: $"x.{language}.srt",
            HearingImpaired: false, Reason: "Matched.", Match: SubtitleCutoff.Rung, SourceKey: sourceKey);

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
        SqliteJobStore JobStore,
        LibraryItem Library,
        FixedTimeProvider TimeProvider)> CreateAsync(ISubtitleFetchService fetchService)
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
        var handler = new LibrarySubtitleSearchJobHandler(libraries, subtitles, fetchService, jobStore, publisher, jobStore);

        return (handler, movies, subtitles, jobStore, library!, timeProvider);
    }

    /// <summary>Hands back outcomes in order, one per call, and remembers the
    /// blocked keys it was asked to skip so the test can assert the wiring
    /// actually reaches the provider search.</summary>
    private sealed class QueueFetchService(params SubtitleFetchOutcome[] outcomes) : ISubtitleFetchService
    {
        private int _index;
        public List<IReadOnlyCollection<string>> BlockedKeysSeenByCall { get; } = [];

        public Task<SubtitleFetchOutcome> FetchAsync(
            SubtitleSearchRequest request,
            string language,
            bool isEpisodeMedia,
            string videoPath,
            bool excludeHearingImpaired,
            CancellationToken cancellationToken,
            SubtitleContentModificationPolicy? contentPolicy = null,
            SubtitleNamePolicy? namePolicy = null,
            bool omitLanguageCode = false,
            IReadOnlyCollection<string>? blockedKeys = null)
        {
            BlockedKeysSeenByCall.Add(blockedKeys ?? []);
            var outcome = outcomes[Math.Min(_index, outcomes.Length - 1)];
            _index++;
            return Task.FromResult(outcome);
        }

        public Task<IReadOnlyList<SubtitleSearchCandidate>> SearchCandidatesAsync(
            SubtitleSearchRequest request,
            string language,
            bool isEpisodeMedia,
            string? videoPath,
            bool excludeHearingImpaired,
            CancellationToken cancellationToken,
            SubtitleNamePolicy? namePolicy = null,
            IReadOnlyCollection<string>? blockedKeys = null)
            => Task.FromResult<IReadOnlyList<SubtitleSearchCandidate>>([]);

        public Task<SubtitleFetchOutcome> DownloadCandidateAsync(
            SubtitleSearchRequest request,
            string providerKey,
            string downloadToken,
            string language,
            bool isEpisodeMedia,
            string videoPath,
            CancellationToken cancellationToken,
            SubtitleContentModificationPolicy? contentPolicy = null,
            SubtitleNamePolicy? namePolicy = null,
            bool omitLanguageCode = false)
            => Task.FromResult(outcomes[0]);
    }
}
