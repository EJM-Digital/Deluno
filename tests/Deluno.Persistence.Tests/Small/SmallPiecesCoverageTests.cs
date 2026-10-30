using System.Reflection;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Search;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Media;
using Deluno.Persistence.Tests.Support;
using Deluno.Quality;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Deluno.Series.Services;
using Deluno.Worker.Security;

namespace Deluno.Persistence.Tests.Small;

/// <summary>
/// #1268: small production pieces that had no test of their own: the episode filter for season
/// searches, the hand-send duplicate check, the bulk search, the episode workflow, the startup
/// phases and the Activity line for the local-network sign-in switch.
/// </summary>
public sealed class SmallPiecesCoverageTests
{
    private static readonly CancellationToken Token = CancellationToken.None;

    // ---- MonitoredEpisodeFilter ------------------------------------------------------------

    private static MediaSearchCandidate Candidate(string release)
        => new(release, "ix", "Indexer", "WEB 1080p", 10, true, "summary");

    [Fact]
    public void Nothing_is_covered_when_no_episode_is_wanted()
    {
        Assert.False(MonitoredEpisodeFilter.ReleaseCoversManagedEpisodes("Show.S01.1080p.WEB-DL-GRP", []));
    }

    [Fact]
    public void A_season_pack_covers_a_wanted_episode_only_of_its_own_season()
    {
        var wanted = new[] { new MonitoredEpisodeFilter.WantedEpisode(1, 3) };

        Assert.True(MonitoredEpisodeFilter.ReleaseCoversManagedEpisodes("Show.Name.S01.1080p.WEB-DL-GRP", wanted));
        Assert.False(MonitoredEpisodeFilter.ReleaseCoversManagedEpisodes("Show.Name.S02.1080p.WEB-DL-GRP", wanted));
    }

    [Fact]
    public void A_single_episode_covers_only_the_wanted_episode_it_is()
    {
        var wanted = new[] { new MonitoredEpisodeFilter.WantedEpisode(2, 5) };

        Assert.True(MonitoredEpisodeFilter.ReleaseCoversManagedEpisodes("Show.Name.S02E05.1080p.WEB-DL-GRP", wanted));
        Assert.False(MonitoredEpisodeFilter.ReleaseCoversManagedEpisodes("Show.Name.S02E06.1080p.WEB-DL-GRP", wanted));
        Assert.False(MonitoredEpisodeFilter.ReleaseCoversManagedEpisodes("Show.Name.S01E05.1080p.WEB-DL-GRP", wanted));
    }

    [Fact]
    public void A_release_that_is_neither_a_season_nor_an_episode_passes_when_something_is_wanted()
    {
        var wanted = new[] { new MonitoredEpisodeFilter.WantedEpisode(1, 1) };

        Assert.True(MonitoredEpisodeFilter.ReleaseCoversManagedEpisodes("Some.Documentary.1080p.WEB-DL-GRP", wanted));
    }

    [Fact]
    public void Filtering_keeps_the_candidates_that_cover_a_wanted_episode_and_passes_all_when_none_are_wanted()
    {
        var candidates = new[]
        {
            Candidate("Show.Name.S01.1080p.WEB-DL-GRP"),
            Candidate("Show.Name.S02.1080p.WEB-DL-GRP"),
            Candidate("Show.Name.S01E02.1080p.WEB-DL-GRP"),
        };

        var kept = MonitoredEpisodeFilter.Filter(candidates, [new MonitoredEpisodeFilter.WantedEpisode(1, 2)]);

        Assert.Equal(["Show.Name.S01.1080p.WEB-DL-GRP", "Show.Name.S01E02.1080p.WEB-DL-GRP"], kept.Select(c => c.ReleaseName));
        Assert.Same(candidates, MonitoredEpisodeFilter.Filter(candidates, []));
    }

    [Fact]
    public void The_season_pack_detector_classifies_and_checks_coverage_like_the_quality_classifier()
    {
        var season = SeasonPackDetector.Classify("Show.Name.S03.1080p.WEB-DL-GRP");
        Assert.True(season.IsSeason);
        Assert.Equal(3, season.Season);
        Assert.True(SeasonPackDetector.CoversEpisode("Show.Name.S03.1080p.WEB-DL-GRP", 3, 4));
        Assert.False(SeasonPackDetector.CoversEpisode("Show.Name.S03E02.1080p.WEB-DL-GRP", 3, 4));
    }

    // ---- ActiveDownloadChecks --------------------------------------------------------------

    [Fact]
    public void The_whole_title_check_matches_the_same_title_ignoring_case_and_no_other()
    {
        var check = ActiveDownloadChecks.ForWholeTitle("Movie-1", "the film", InterfaceStub.Create<IDownloadClientTelemetryService>());

        Assert.Equal("the film", check.Subject);
        Assert.True(check.IsSameTarget(new DownloadInFlight("movie", "movie-1", "Rel")));
        Assert.False(check.IsSameTarget(new DownloadInFlight("movie", "movie-2", "Rel")));
    }

    [Fact]
    public async Task The_whole_title_check_replaces_through_the_telemetry_service()
    {
        var removed = new List<(string ClientId, string Item, bool FromClient, bool Blocklist)>();
        var check = ActiveDownloadChecks.ForWholeTitle("m1", "the film", Api.AHandSendForAFilmTests.TelemetryStub.Create(removed));

        var error = await check.ReplaceAsync(new ActiveDownloadConflict("c1", "qBittorrent", "hash", "First.Release", "the film"), Token);

        Assert.Null(error);
        Assert.Equal([("c1", "hash", true, false)], removed);
    }

    [Fact]
    public async Task A_replace_the_client_refuses_says_why()
    {
        var telemetry = DispatchProxy.Create<IDownloadClientTelemetryService, RefusingTelemetry>();

        var error = await ActiveDownloadChecks.ReplaceAsync(
            telemetry, new ActiveDownloadConflict("c1", "qBittorrent", "hash", "First.Release", "the film"), Token);

        Assert.Equal("The first download could not be removed from qBittorrent: The client said no.", error);
    }

    public class RefusingTelemetry : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Assert.Equal("RemoveWithOptionsAsync", targetMethod!.Name);
            return Task.FromResult(new QueueRemovalResult((string)args![0]!, (string)args[1]!, "failed", false, "The client said no.", false, false, null, null));
        }
    }

    // ---- MediaBulkSearchHandler and TitleSearchJobs ----------------------------------------

    private static MediaWantedItem Wanted(string id, string title, string libraryId)
        => new(id, title, 2020, null, libraryId, "wanted", "missing", false, null, null, false, null, null, null, null, false, null, DateTimeOffset.UnixEpoch);

    [Theory]
    [InlineData(MediaKind.Movie, "movieIds", "movie")]
    [InlineData(MediaKind.Series, "seriesIds", "series")]
    public async Task A_bulk_search_with_nothing_chosen_names_the_missing_field(MediaKind kind, string field, string noun)
    {
        var state = InterfaceStub.Create<IMediaStateRepository>();
        var scheduler = InterfaceStub.Create<IJobScheduler>();

        foreach (IReadOnlyList<string>? none in new IReadOnlyList<string>?[] { null, [] })
        {
            var result = await MediaBulkSearchHandler.ExecuteAsync(kind, none, state, scheduler, Token);

            Assert.Equal(0, result.SearchesTriggered);
            Assert.Equal(0, result.LibraryCount);
            Assert.Equal($"Choose at least one {noun} to search for.", Assert.Single(result.ValidationErrors![field]));
        }

        Assert.Empty(InterfaceStub.Of(state).Calls);
        Assert.Empty(InterfaceStub.Of(scheduler).Calls);
    }

    [Fact]
    public async Task A_bulk_search_queues_one_job_per_wanted_title_with_a_library_and_counts_the_libraries()
    {
        var queued = new List<EnqueueJobRequest>();
        var state = InterfaceStub.Create<IMediaStateRepository>((name, args) =>
        {
            Assert.Equal("ListWantedByIdsAsync", name);
            Assert.Equal(MediaKind.Movie, args[0]);
            IReadOnlyList<MediaWantedItem> rows =
            [
                Wanted("m1", "One", "lib-a"),
                Wanted("m2", "Two", "LIB-A"),
                Wanted("m3", "Three", "lib-b"),
                Wanted("m4", "No library", " "),
            ];
            return Task.FromResult(rows);
        });
        var scheduler = InterfaceStub.Create<IJobScheduler>((name, args) =>
        {
            queued.Add((EnqueueJobRequest)args[0]!);
            return Task.FromResult<JobQueueItem>(null!);
        });

        var result = await MediaBulkSearchHandler.ExecuteAsync(MediaKind.Movie, ["m1", "m2", "m3", "m4"], state, scheduler, Token);

        Assert.Null(result.ValidationErrors);
        Assert.Equal(3, result.SearchesTriggered);
        Assert.Equal(2, result.LibraryCount);
        Assert.Equal(["m1", "m2", "m3"], queued.Select(q => q.RelatedEntityId));
        var first = queued[0];
        Assert.Equal(TitleSearchJobs.Movie, first.JobType);
        Assert.Equal("bulk", first.Source);
        Assert.Equal("movie", first.RelatedEntityType);
        Assert.Equal("bulk-search:movies.search.manual:m1:lib-a", first.DedupeKey);
        Assert.Contains("\"title\":\"One\"", first.PayloadJson);
        Assert.Contains(TitleSearchJobs.FromBulkSearch, first.PayloadJson);
    }

    [Fact]
    public void Title_search_jobs_name_the_job_kind_payload_and_dedupe_key_per_media_kind()
    {
        Assert.Equal(TitleSearchJobs.Series, TitleSearchJobs.JobTypeFor(MediaKind.Series));
        Assert.Equal(TitleSearchJobs.Movie, TitleSearchJobs.JobTypeFor(MediaKind.Movie));
        Assert.Equal("bulk-search:series.search.manual:s1:lib", TitleSearchJobs.DedupeKey(MediaKind.Series, "s1", "lib"));
        Assert.Equal("{\"id\":\"s1\",\"title\":\"T\",\"libraryId\":null,\"startedBy\":\"the bulk editor\"}",
            TitleSearchJobs.Payload("s1", "T", null, TitleSearchJobs.FromBulkEditor));
    }

    // ---- EpisodeWorkflowService ------------------------------------------------------------

    private static SeriesEpisodeInventoryItem Episode(string id, bool monitored, bool hasFile, bool cutoffMet)
        => new(id, 1, 1, null, null, null, monitored, hasFile, "wanted", "reason", cutoffMet, null, null, false, null, null, null, DateTimeOffset.UnixEpoch);

    private static IEpisodeWorkflowService Workflow(SeriesInventoryDetail? inventory, string? currentQuality = null, Func<string?, int>? rank = null)
    {
        var repository = InterfaceStub.Create<ISeriesCatalogRepository>((name, args) => name switch
        {
            "GetInventoryDetailAsync" => Task.FromResult(inventory),
            "GetEpisodeCurrentQualityAsync" => Task.FromResult(currentQuality),
            _ => throw new NotSupportedException(name),
        });
        var policy = InterfaceStub.Create<IVersionedMediaPolicyEngine>((name, args) => name switch
        {
            "QualityRank" => rank!((string?)args[0]),
            _ => throw new NotSupportedException(name),
        });
        return new EpisodeWorkflowService(repository, policy);
    }

    private static SeriesInventoryDetail Inventory(params SeriesEpisodeInventoryItem[] episodes)
        => new("s1", "Show", 2020, 1, episodes.Length, 0, episodes);

    [Fact]
    public async Task An_unknown_series_or_episode_is_reported_unknown_with_the_reason()
    {
        var noSeries = await Workflow(null).EvaluateEpisodeAsync("e1", "s1", "lib", Token);
        var noEpisode = await Workflow(Inventory(Episode("other", true, false, false))).EvaluateEpisodeAsync("e1", "s1", "lib", Token);

        Assert.Equal(("unknown", "Series not found"), (noSeries.Decision, noSeries.Reason));
        Assert.Equal(("unknown", "Episode not found"), (noEpisode.Decision, noEpisode.Reason));
        Assert.Equal("e1", noEpisode.EpisodeId);
    }

    [Theory]
    [InlineData(true, true, true, "archived", "Has file and quality cutoff met")]
    [InlineData(false, true, true, "archived", "Has file and quality cutoff met")]
    [InlineData(true, false, false, "wanted", "Monitored but missing")]
    [InlineData(true, false, true, "wanted", "Monitored but missing")]
    [InlineData(true, true, false, "wanted", "Has file but quality below cutoff")]
    [InlineData(false, false, false, "satisfied", "No action needed")]
    [InlineData(false, true, false, "satisfied", "No action needed")]
    public async Task An_episode_is_archived_wanted_or_satisfied_by_its_file_cutoff_and_monitoring(
        bool monitored, bool hasFile, bool cutoffMet, string decision, string reason)
    {
        var result = await Workflow(Inventory(Episode("e1", monitored, hasFile, cutoffMet))).EvaluateEpisodeAsync("e1", "s1", "lib", Token);

        Assert.Equal(decision, result.Decision);
        Assert.Equal(reason, result.Reason);
    }

    [Fact]
    public async Task The_quality_delta_is_candidate_rank_minus_current_rank()
    {
        var workflow = Workflow(null, "WEB 720p", quality => quality == "WEB 720p" ? 3 : 7);

        Assert.Equal(4, await workflow.CalculateEpisodeQualityDeltaAsync("e1", "lib", "Bluray 1080p", Token));
    }

    [Theory]
    [InlineData(null, "Bluray 1080p")]
    [InlineData("", "Bluray 1080p")]
    [InlineData("WEB 720p", "")]
    [InlineData("WEB 720p", "  ")]
    public async Task There_is_no_quality_delta_when_either_quality_is_missing(string? current, string candidate)
    {
        Assert.Null(await Workflow(null, current, _ => 5).CalculateEpisodeQualityDeltaAsync("e1", "lib", candidate, Token));
    }

    [Theory]
    [InlineData(-1, 4)]
    [InlineData(4, -1)]
    public async Task There_is_no_quality_delta_when_a_quality_has_no_rank(int currentRank, int candidateRank)
    {
        var workflow = Workflow(null, "Current", quality => quality == "Current" ? currentRank : candidateRank);

        Assert.Null(await workflow.CalculateEpisodeQualityDeltaAsync("e1", "lib", "Candidate", Token));
    }

    // ---- StartupPhases ---------------------------------------------------------------------

    [Theory]
    [InlineData(StartupPhase.Starting, "starting", "starting")]
    [InlineData(StartupPhase.PreUpdateBackup, "backup", "backing up your data before the update")]
    [InlineData(StartupPhase.OpeningDatabases, "databases", "opening the databases")]
    [InlineData(StartupPhase.Migrations, "migrations", "updating the databases")]
    [InlineData(StartupPhase.WebHost, "web-host", "starting the web server")]
    [InlineData(StartupPhase.Ready, "ready", "ready")]
    public void Each_startup_phase_has_a_log_key_and_plain_words_and_the_key_parses_back(StartupPhase phase, string key, string words)
    {
        Assert.Equal(key, StartupPhases.Key(phase));
        Assert.Equal(words, StartupPhases.Words(phase));
        Assert.True(StartupPhases.TryParse(key, out var parsed));
        Assert.Equal(phase, parsed);
    }

    [Fact]
    public void An_unknown_startup_key_does_not_parse_and_an_unknown_phase_is_worded_by_its_name()
    {
        Assert.False(StartupPhases.TryParse("nonsense", out var phase));
        Assert.Equal(default, phase);
        Assert.False(StartupPhases.TryParse(null, out _));
        Assert.Equal("Starting".ToLowerInvariant(), StartupPhases.Key(StartupPhase.Starting));

        var beyond = (StartupPhase)99;
        Assert.Equal("99", StartupPhases.Key(beyond));
        Assert.Equal("99", StartupPhases.Words(beyond));
    }

    // ---- ActivityLocalNetworkAuthChangeNotifier --------------------------------------------

    [Theory]
    [InlineData(true, "Skip sign-in for devices on the local network turned on.")]
    [InlineData(false, "Skip sign-in for devices on the local network turned off.")]
    public async Task Turning_the_local_network_sign_in_skip_on_or_off_is_written_to_activity_in_plain_words(bool enabled, string message)
    {
        var recorded = new List<object?[]>();
        var activity = InterfaceStub.Create<IActivityFeedRepository>((name, args) =>
        {
            Assert.Equal("RecordActivityAsync", name);
            recorded.Add(args);
            return Task.FromResult<ActivityEventItem>(null!);
        });

        await new ActivityLocalNetworkAuthChangeNotifier(activity).NotifyChangedAsync(enabled, Token);

        var args = Assert.Single(recorded);
        Assert.Equal("security.local-network-auth", args[0]);
        Assert.Equal(message, args[1]);
        Assert.Null(args[2]);
    }
}
