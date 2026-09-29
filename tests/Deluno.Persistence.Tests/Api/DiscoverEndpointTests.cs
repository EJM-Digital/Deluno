using System.Net;
using System.Net.Http.Json;
using Deluno.Api.Discover;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Intake.Contracts;
using Deluno.Intake.Data;
using Deluno.Integrations.Metadata;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Media;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Quality;
using Deluno.Quality.Contracts;
using Deluno.Quality.Data;
using Deluno.Quality.ReleasePreferences;
using Deluno.Security;
using Deluno.Security.Contracts;
using Deluno.Security.Data;
using Deluno.Series.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #785: Discover surfaces TMDb's own "popular" list and every configured
/// import list's entries, marks what the library already holds or has
/// excluded, and adds a title from either with the same root/quality-profile/
/// monitor choices the existing add-title flow offers.
/// </summary>
public sealed class DiscoverEndpointTests : IAsyncDisposable
{
    private readonly TestStorage _storage;
    private readonly IHost _host;
    private readonly HttpClient _client;
    private readonly IMovieCatalogRepository _movies;
    private readonly ISeriesCatalogRepository _series;
    private readonly ILibrariesRepository _libraries;
    private readonly IIntakeRepository _intake;
    private readonly IUnifiedExclusionRepository _exclusions;
    private readonly FakeIntakeListPreviewService _intakePreview = new();
    private readonly FakeJobScheduler _jobScheduler = new();

    public DiscoverEndpointTests()
    {
        _storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-27T09:00:00Z"));
        var migrator = new SqliteDatabaseMigrator(_storage.Factory, timeProvider);

        new PlatformSchemaInitializer(_storage.Factory, migrator, NullLogger<PlatformSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        new SeriesSchemaInitializer(_storage.Factory, migrator, NullLogger<SeriesSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        new MoviesSchemaInitializer(_storage.Factory, migrator, NullLogger<MoviesSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None).GetAwaiter().GetResult();

        _movies = new SqliteMovieCatalogRepository(_storage.Factory, timeProvider);
        _series = new SqliteSeriesCatalogRepository(_storage.Factory, timeProvider);
        _libraries = new SqliteLibrariesRepository(_storage.Factory, timeProvider);
        _intake = new SqliteIntakeRepository(_storage.Factory, timeProvider);
        _exclusions = new SqliteUnifiedExclusionRepository(_storage.Factory, timeProvider);
        var mediaState = new SqliteMediaStateRepository(_storage.Factory, timeProvider);
        IMetadataLibraryPresence libraryPresence = new MediaStateLibraryPresence(mediaState);
        IMediaDecisionService mediaDecisionService = new MediaDecisionService(new VersionedMediaPolicyEngine());
        var security = new SqliteSecurityRepository(_storage.Factory, timeProvider);

        var builder = new HostBuilder().ConfigureWebHost(webBuilder =>
        {
            webBuilder
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddAuthorization();
                    services.AddSingleton<TimeProvider>(timeProvider);
                    services.AddSingleton<ISecurityRepository>(security);
                    services.AddSingleton(_movies);
                    services.AddSingleton(_series);
                    services.AddSingleton(_libraries);
                    services.AddSingleton(_intake);
                    services.AddSingleton(_exclusions);
                    services.AddSingleton(libraryPresence);
                    services.AddSingleton(mediaDecisionService);
                    services.AddSingleton<IIntakeListPreviewService>(_intakePreview);
                    services.AddSingleton<IMetadataProvider>(new RecordingMetadataProvider(PopularCards));
                    services.AddSingleton<IAutoTaggingService>(new NoRulesAutoTagging());
                    services.AddSingleton<IJobScheduler>(_jobScheduler);
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapDelunoDiscoverEndpoints());
                });
        });

        _host = builder.Build();
        _host.StartAsync().GetAwaiter().GetResult();
        _client = _host.GetTestServer().CreateClient();

        var apiKey = security.CreateApiKeyAsync(new CreateApiKeyRequest("Discover tests", "read"), CancellationToken.None)
            .GetAwaiter().GetResult();
        _client.DefaultRequestHeaders.Add("X-Api-Key", apiKey.ApiKey);
    }

    private static IReadOnlyList<MetadataSearchResult> PopularCards { get; } =
    [
        new MetadataSearchResult(
            Provider: "tmdb", ProviderId: "603", MediaType: "movies", Title: "The Matrix", OriginalTitle: null,
            Year: 1999, Overview: "A hacker learns the truth.", PosterUrl: "https://image.tmdb.org/matrix.jpg",
            BackdropUrl: null, Rating: 8.2, Ratings: [], Genres: [], ImdbId: "tt0133093", ExternalUrl: null),
        new MetadataSearchResult(
            Provider: "tmdb", ProviderId: "27205", MediaType: "movies", Title: "Inception", OriginalTitle: null,
            Year: 2010, Overview: "A thief who steals secrets.", PosterUrl: "https://image.tmdb.org/inception.jpg",
            BackdropUrl: null, Rating: 8.4, Ratings: [], Genres: [], ImdbId: "tt1375666", ExternalUrl: null)
    ];

    private async Task<LibraryItem> CreateMovieLibraryAsync(string? qualityProfileId = null)
        => await _libraries.CreateLibraryAsync(
            new CreateLibraryRequest(
                Name: "Movies",
                MediaType: "movies",
                Purpose: "Main",
                RootPath: Path.Combine(_storage.DataRoot, "movies"),
                DownloadsPath: null,
                QualityProfileId: qualityProfileId,
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

    [Fact]
    public async Task Popular_marks_a_title_already_in_the_library_and_a_title_that_is_excluded()
    {
        // "The Matrix" is already in the library.
        await _movies.AddAsync(new CreateMovieRequest("The Matrix", 1999, "tt0133093"), CancellationToken.None);

        // "Inception" was previously excluded by tmdb id.
        await _exclusions.UpsertAsync(
            new UpsertMediaExclusionRequest(
                MediaType: "movies",
                SourceKind: MediaExclusionSourceKinds.ImportList,
                SourceId: "source-1",
                SourceName: "Some list",
                Provider: "tmdb:27205",
                EntryKey: "tmdb:27205",
                Title: "Inception",
                Year: 2010,
                ImdbId: null,
                DurationDays: null,
                Reason: "Not interested"),
            CancellationToken.None);

        var response = await _client.GetAsync("/api/discover?mediaType=movies");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<DiscoverResponse>();

        var matrix = Assert.Single(body!.Popular, item => item.Title == "The Matrix");
        Assert.True(matrix.InLibrary);
        Assert.NotNull(matrix.LibraryEntryId);
        Assert.False(matrix.Excluded);

        var inception = Assert.Single(body.Popular, item => item.Title == "Inception");
        Assert.False(inception.InLibrary);
        Assert.True(inception.Excluded);
    }

    [Fact]
    public async Task Lists_section_reflects_each_configured_sources_own_preview()
    {
        var source = await _intake.CreateIntakeSourceAsync(
            new CreateIntakeSourceRequest(
                Name: "Trakt Popular",
                Provider: "trakt",
                FeedUrl: "https://example.test/list",
                MediaType: "movies",
                LibraryId: null,
                QualityProfileId: null,
                RequiredGenres: null,
                MinimumRating: null,
                MinimumYear: null,
                MaximumAgeDays: null,
                AllowedCertifications: null,
                Audience: null,
                SyncIntervalHours: null,
                SearchOnAdd: false,
                IsEnabled: true),
            CancellationToken.None);

        _intakePreview.Result = new IntakeListPreviewResult(
            source.Id, source.Name, "trakt", "movies", null, 2, 2, false,
            [
                new IntakeListPreviewItem("Would Add Movie", 2020, "movies", null, "would add", "Passes filters.", "high"),
                new IntakeListPreviewItem("Already Held Movie", 2019, "movies", null, "already in library", "Already have it.", "high")
            ],
            []);

        var response = await _client.GetAsync("/api/discover?mediaType=movies");
        var body = await response.Content.ReadFromJsonAsync<DiscoverResponse>();

        var section = Assert.Single(body!.Lists);
        Assert.Equal("Trakt: list", section.SourceName);
        Assert.Equal(2, section.Items.Count);
        Assert.Contains(section.Items, item => item.Title == "Already Held Movie" && item.InLibrary);
        Assert.Contains(section.Items, item => item.Title == "Would Add Movie" && !item.InLibrary && !item.Excluded);
    }

    [Fact]
    public async Task Add_from_discover_creates_the_title_in_the_chosen_library_with_the_chosen_profile_and_monitors_it()
    {
        var qualityProfiles = new SqliteQualityRepository(_storage.Factory, new FixedTimeProvider(DateTimeOffset.Parse("2026-09-27T09:00:00Z")));
        var profile = await qualityProfiles.CreateQualityProfileAsync(
            new CreateQualityProfileRequest(
                Name: "4K profile",
                MediaType: "movies",
                CutoffQuality: null,
                AllowedQualities: null,
                CustomFormatIds: null,
                UpgradeUntilCutoff: true,
                UpgradeUnknownItems: true),
            CancellationToken.None);
        var library = await CreateMovieLibraryAsync();

        var response = await _client.PostAsJsonAsync("/api/discover/add", new DiscoverAddRequest(
            MediaType: "movies",
            Title: "The Matrix",
            Year: 1999,
            ImdbId: "tt0133093",
            Provider: "tmdb",
            ProviderId: "603",
            Overview: "A hacker learns the truth.",
            PosterUrl: "https://image.tmdb.org/matrix.jpg",
            BackdropUrl: null,
            LibraryId: library.Id,
            QualityProfileId: profile.Id,
            Monitored: true));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var added = await response.Content.ReadFromJsonAsync<DiscoverAddResponse>();
        Assert.NotNull(added);

        var movie = await _movies.GetByIdAsync(added!.Id, CancellationToken.None);
        Assert.NotNull(movie);
        Assert.True(movie!.Monitored);

        await using var connection = await _storage.Factory.OpenConnectionAsync(Infrastructure.Storage.DelunoDatabaseNames.Movies, CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT quality_profile_id FROM movie_entries WHERE id = @id;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@id";
        parameter.Value = movie.Id;
        command.Parameters.Add(parameter);
        var storedProfileId = (string?)await command.ExecuteScalarAsync(CancellationToken.None);
        Assert.Equal(profile.Id, storedProfileId);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
        _storage.Dispose();
    }

    [Fact]
    public async Task Add_from_discover_with_search_for_missing_now_dispatches_a_targeted_search_job()
    {
        var library = await CreateMovieLibraryAsync();

        var response = await _client.PostAsJsonAsync("/api/discover/add", new DiscoverAddRequest(
            MediaType: "movies",
            Title: "Arrival",
            Year: 2016,
            ImdbId: "tt2543164",
            Provider: "tmdb",
            ProviderId: "329865",
            Overview: null,
            PosterUrl: null,
            BackdropUrl: null,
            LibraryId: library.Id,
            QualityProfileId: null,
            Monitored: false,
            SearchForMissingNow: true,
            SearchForCutoffUnmetNow: false));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var added = await response.Content.ReadFromJsonAsync<DiscoverAddResponse>();

        var refreshJob = Assert.Single(_jobScheduler.Requests, request => request.JobType == "movies.catalog.refresh");
        Assert.Contains("\"pendingSearches\":[{", refreshJob.PayloadJson);
        Assert.Contains(added!.Id, refreshJob.PayloadJson);
    }

    private sealed class FakeIntakeListPreviewService : IIntakeListPreviewService
    {
        public IntakeListPreviewResult? Result { get; set; }

        public Task<IntakeListPreviewResult> PreviewAsync(string sourceId, CancellationToken cancellationToken)
            => Result is null
                ? throw new InvalidOperationException("No preview configured for this test.")
                : Task.FromResult(Result);
    }

    private sealed class FakeJobScheduler : IJobScheduler
    {
        public List<EnqueueJobRequest> Requests { get; } = [];

        public Task<JobQueueItem> EnqueueAsync(EnqueueJobRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new JobQueueItem(
                Id: "job-" + Requests.Count,
                JobType: request.JobType,
                Source: request.Source,
                Status: "pending",
                PayloadJson: request.PayloadJson,
                Attempts: 0,
                CreatedUtc: DateTimeOffset.UnixEpoch,
                ScheduledUtc: request.ScheduledUtc ?? DateTimeOffset.UnixEpoch,
                StartedUtc: null,
                CompletedUtc: null,
                LeasedUntilUtc: null,
                WorkerId: null,
                LastError: null,
                RelatedEntityType: request.RelatedEntityType,
                RelatedEntityId: request.RelatedEntityId,
                IdempotencyKey: request.IdempotencyKey,
                DedupeKey: request.DedupeKey,
                MaxAttempts: request.MaxAttempts ?? 1,
                LastAttemptUtc: null,
                NextAttemptUtc: null));
        }
    }
}

/// <summary>The Discover tests carry no auto-tagging rules (#1024), so the rules service answers "no change".</summary>
internal sealed class NoRulesAutoTagging : IAutoTaggingService
{
    public Task<AutoTagOutcome> ApplyAsync(MediaKind kind, string mediaId, AutoTagTrigger trigger, AutoTagAddFacts? add, IReadOnlyList<string>? libraryIds, CancellationToken cancellationToken) =>
        Task.FromResult(AutoTagOutcome.None);

    public Task<IReadOnlyList<string>> PredictAsync(AutoTagFacts facts, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    public Task<int> ApplyToAllAsync(MediaKind? kind, CancellationToken cancellationToken) => Task.FromResult(0);
}
