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
/// The per-language hearing-impaired and forced-track preference (#835,
/// #836): it survives a save and a re-read, and the search handler reads a
/// wanted language's own row instead of the hard-coded "always plain
/// preferred, never forced" every library got before this existed.
/// </summary>
public sealed class LibrarySubtitleTypePreferenceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-27T00:00:00Z");

    [Fact]
    public async Task Saving_a_library_keeps_its_per_language_preferences()
    {
        var (libraries, libraryId, _, _) = await CreateAsync();

        var saved = await libraries.UpdateLibrarySubtitlesAsync(
            libraryId,
            new UpdateLibrarySubtitlesRequest(
                Languages: ["en", "ja"],
                Mode: "all",
                LanguageTypePreferences:
                [
                    new SubtitleLanguageTypePreference("en", SubtitleHearingImpairedPreferences.PlainOnly),
                    new SubtitleLanguageTypePreference("ja", SubtitleHearingImpairedPreferences.HearingImpairedPreferred, IncludeForced: true)
                ]),
            CancellationToken.None);

        Assert.NotNull(saved);
        var preferences = saved!.SubtitleLanguageTypePreferences!;
        Assert.Equal(2, preferences.Count);
        var en = preferences.Single(p => p.Language == "en");
        Assert.Equal(SubtitleHearingImpairedPreferences.PlainOnly, en.HearingImpaired);
        Assert.False(en.IncludeForced);
        var ja = preferences.Single(p => p.Language == "ja");
        Assert.Equal(SubtitleHearingImpairedPreferences.HearingImpairedPreferred, ja.HearingImpaired);
        Assert.True(ja.IncludeForced);

        // And it is still there on a fresh read, not just the object the
        // update handed back.
        var reloaded = (await libraries.ListLibrariesAsync(CancellationToken.None)).Single(item => item.Id == libraryId);
        Assert.Equal(2, reloaded.SubtitleLanguageTypePreferences!.Count);
    }

    [Fact]
    public async Task A_language_with_no_row_gets_the_old_defaults()
    {
        var (libraries, libraryId, _, _) = await CreateAsync();

        var saved = await libraries.UpdateLibrarySubtitlesAsync(
            libraryId,
            new UpdateLibrarySubtitlesRequest(Languages: ["en"], Mode: "all"),
            CancellationToken.None);

        var preference = SubtitleLanguageTypePreferenceCodec.For(
            saved!.SubtitleLanguageTypePreferences ?? [], "en");
        Assert.Equal(SubtitleHearingImpairedPreferences.PlainPreferred, preference.HearingImpaired);
        Assert.False(preference.IncludeForced);
    }

    [Fact]
    public async Task The_search_handler_asks_with_each_languages_own_preference()
    {
        var (libraries, libraryId, movies, jobStore) = await CreateAsync();
        await libraries.UpdateLibrarySubtitlesAsync(
            libraryId,
            new UpdateLibrarySubtitlesRequest(
                Languages: ["en"],
                Mode: "all",
                LanguageTypePreferences: [new SubtitleLanguageTypePreference("en", SubtitleHearingImpairedPreferences.PlainOnly)]),
            CancellationToken.None);

        await movies.ImportExistingBatchAsync(
            libraryId,
            [
                new ExistingMovieImportRequest(
                    Title: "Dune",
                    ReleaseYear: 2021,
                    WantedStatus: WantedStatuses.Covered,
                    WantedReason: "Imported from your existing library.",
                    CurrentQuality: "WEB 2160p",
                    TargetQuality: "WEB 2160p",
                    QualityCutoffMet: true,
                    FilePath: @"D:\Media\Dune\Dune.mkv",
                    FileSizeBytes: 8_000_000_000)
            ],
            CancellationToken.None);

        var subtitles = new SqliteMediaSubtitleRepository(jobStore.Storage, jobStore.TimeProvider);
        var recorder = new RecordingFetchService();
        var handler = new LibrarySubtitleSearchJobHandler(
            libraries, subtitles, recorder, jobStore.JobStore,
            jobStore.JobStore);

        await handler.HandleAsync(JobFor(libraryId), CancellationToken.None);

        Assert.Equal(SubtitleHearingImpairedPreferences.PlainOnly, Assert.Single(recorder.PreferencesAsked));
    }

    [Fact]
    public async Task A_wanted_forced_track_is_fetched_and_recorded_as_forced_never_as_coverage()
    {
        var (libraries, libraryId, movies, jobs) = await CreateAsync();
        await libraries.UpdateLibrarySubtitlesAsync(
            libraryId,
            new UpdateLibrarySubtitlesRequest(
                Languages: ["en"],
                Mode: "all",
                LanguageTypePreferences: [new SubtitleLanguageTypePreference("en", IncludeForced: true)]),
            CancellationToken.None);

        await movies.ImportExistingBatchAsync(
            libraryId,
            [
                new ExistingMovieImportRequest(
                    Title: "Dune",
                    ReleaseYear: 2021,
                    WantedStatus: WantedStatuses.Covered,
                    WantedReason: "Imported from your existing library.",
                    CurrentQuality: "WEB 2160p",
                    TargetQuality: "WEB 2160p",
                    QualityCutoffMet: true,
                    FilePath: @"D:\Media\Dune\Dune.mkv",
                    FileSizeBytes: 8_000_000_000)
            ],
            CancellationToken.None);

        var subtitles = new SqliteMediaSubtitleRepository(jobs.Storage, jobs.TimeProvider);
        var fetchService = new ForcedFoundFetchService();
        var handler = new LibrarySubtitleSearchJobHandler(
            libraries, subtitles, fetchService, jobs.JobStore,
            jobs.JobStore);

        await handler.HandleAsync(JobFor(libraryId), CancellationToken.None);

        var movieId = (await movies.ListPageAsync(new CatalogueQuery(Search: "Dune"), CancellationToken.None)).Items.Single().Id;
        var stored = await subtitles.ListSubtitlesAsync(MediaKind.Movie, movieId, CancellationToken.None);
        var forced = Assert.Single(stored, row => row.Forced);
        Assert.Equal("en", forced.Language);
        Assert.False(forced.HearingImpaired);
        Assert.True(fetchService.ForcedAsked);
    }

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

    private static async Task<(
        SqliteLibrariesRepository Libraries,
        string LibraryId,
        SqliteMovieCatalogRepository Movies,
        (IDelunoDatabaseConnectionFactory Storage, TimeProvider TimeProvider, SqliteJobStore JobStore) Jobs)> CreateAsync()
    {
        var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(Now);
        var migrator = new SqliteDatabaseMigrator(storage.Factory, timeProvider);
        await new PlatformSchemaInitializer(storage.Factory, migrator, NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await new MoviesSchemaInitializer(storage.Factory, migrator, NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await new JobsSchemaInitializer(storage.Factory, migrator, NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var libraries = new SqliteLibrariesRepository(storage.Factory, timeProvider);
        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
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

        return (libraries, created.Id, movies, (storage.Factory, timeProvider, jobStore));
    }

    /// <summary>Records which hearing-impaired preference each call asked with, and answers "not found" so the handler moves on.</summary>
    private sealed class RecordingFetchService : ISubtitleFetchService
    {
        public List<string> PreferencesAsked { get; } = [];

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
        {
            PreferencesAsked.Add(hearingImpairedPreference);
            return Task.FromResult(new SubtitleFetchOutcome(
                language, Found: false, ProviderKey: null, WrittenPath: null, HearingImpaired: false,
                Reason: "No provider had it."));
        }

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
            => Task.FromResult(new SubtitleFetchOutcome(language, Found: false, ProviderKey: null, WrittenPath: null, HearingImpaired: false, Reason: "Not asked for in this test."));

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
            => Task.FromResult(new SubtitleFetchOutcome(language, Found: false, ProviderKey: null, WrittenPath: null, HearingImpaired: false, Reason: "Not asked for in this test."));
    }

    /// <summary>Never finds the full subtitle, but always finds a forced one — so the recorded row can only have come from the forced path.</summary>
    private sealed class ForcedFoundFetchService : ISubtitleFetchService
    {
        public bool ForcedAsked { get; private set; }

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
            => Task.FromResult(new SubtitleFetchOutcome(language, Found: false, ProviderKey: null, WrittenPath: null, HearingImpaired: false, Reason: "No provider had it."));

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
        {
            ForcedAsked = true;
            return Task.FromResult(new SubtitleFetchOutcome(
                language, Found: true, ProviderKey: "gestdown", WrittenPath: $"{videoPath}.{language}.forced.srt",
                HearingImpaired: false, Reason: "Fetched.", Match: SubtitleMatch.AnyRelease));
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
            => Task.FromResult(new SubtitleFetchOutcome(language, Found: false, ProviderKey: null, WrittenPath: null, HearingImpaired: false, Reason: "Not asked for in this test."));
    }
}
