using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Integrations.Search;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Quality;
using Deluno.Quality.Contracts;
using Deluno.Quality.ReleasePreferences;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Realtime;

/// <summary>
/// #1098: a search that looks at releases and does not take some of them leaves a record of each, once,
/// by the first thing that turned it down, and pushes <c>ReleasesTurnedDown</c> once with them.
/// </summary>
public sealed class LiveReleasesTurnedDownTests : IDisposable
{
    private readonly TestStorage _storage = TestStorage.Create();
    private readonly FixedTimeProvider _time = new(DateTimeOffset.Parse("2026-10-01T09:00:00Z"));
    private readonly RecordingRealtimeEventPublisher _realtime = new();
    private readonly SqliteTurnedDownReleaseRecorder _recorder;
    private readonly SqliteBlockedReleaseRepository _blocked;

    public LiveReleasesTurnedDownTests()
    {
        new JobsSchemaInitializer(_storage.Factory, new SqliteDatabaseMigrator(_storage.Factory, _time), NullLogger<JobsSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        _recorder = new SqliteTurnedDownReleaseRecorder(_storage.Factory, _time);
        _blocked = new SqliteBlockedReleaseRepository(_storage.Factory, _time);
    }

    public void Dispose() => _storage.Dispose();

    private static MediaSearchCandidate Candidate(string release, string status, string? gate = null, string indexer = "Idx")
        => new(release, "i1", indexer, "Bluray 1080p", 0, true, "x", DecisionStatus: status, RejectionGate: gate);

    private AcquisitionDecisionPipeline Pipeline(params MediaSearchCandidate[] candidates)
        => new(
            new StubPlanner(new MediaSearchPlan(candidates.FirstOrDefault(item => item.DecisionStatus == "preferred"), candidates, "found")),
            blockedReleases: _blocked,
            turnedDownRecorder: _recorder,
            realtimeEventPublisher: _realtime,
            timeProvider: _time);

    private static AcquisitionDecisionRequest Request(bool preview = false, string mediaType = "movies", string? entityId = "movie-sintel")
        => new(
            "Sintel",
            2010,
            mediaType,
            null,
            "Bluray 1080p",
            [new LibrarySourceLinkItem("s1", "lib", "i1", "Idx", 10, "", "", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)],
            [new LibraryDownloadClientLinkItem("c1", "lib", "dc1", "qBittorrent", 10, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)],
            PreviewOnly: preview,
            EntityId: entityId);

    private async Task<List<(string Release, string Group, string? Title, string? EntityId)>> StoredAsync()
    {
        var rows = new List<(string, string, string?, string?)>();
        await using var connection = await _storage.Factory.OpenReadOnlyConnectionAsync(DelunoDatabaseNames.Jobs);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT release_name, reason_group, title, entity_id FROM turned_down_releases ORDER BY rowid;";
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return rows;
    }

    [Fact]
    public async Task A_search_pushes_each_turned_down_release_once_with_its_plain_group_and_leaves_what_it_took()
    {
        var pipeline = Pipeline(
            Candidate("Good.Sintel.2010.1080p", "preferred"),
            Candidate("Cam.Sintel.2010", "rejected", gate: "Release name contains a blocked token such as CAM, Telesync, workprint, or screener."),
            Candidate("Nobody.Sintel.2010", "rejected", gate: "No one is seeding this torrent, so it cannot download."),
            Candidate("Same.Sintel.2010", "equivalent"),
            // The same release offered by a second indexer is one release in this search.
            Candidate("Cam.Sintel.2010", "rejected", gate: "Release name contains a blocked token such as CAM, Telesync, workprint, or screener.", indexer: "Other"));

        await pipeline.PlanAsync(Request());

        var pushed = Assert.Single(_realtime.LiveEventsOf<ReleasesTurnedDownEvent>(LiveEventNames.ReleasesTurnedDown));
        Assert.Equal("Sintel", pushed.Title);
        Assert.Equal("movie", pushed.EntityType);
        Assert.Equal("movie-sintel", pushed.EntityId);
        Assert.Equal(_time.GetUtcNow().UtcDateTime.ToString("O"), pushed.AtUtc);
        Assert.Equal(
            [
                ("Cam.Sintel.2010", "fake", "Fake or mislabelled"),
                ("Nobody.Sintel.2010", "fewSeeders", "Too few people sharing it"),
                ("Same.Sintel.2010", "notBetter", "No better than what you have")
            ],
            pushed.Items.Select(item => (item.Release, item.Group, item.Label)).ToArray());

        var stored = await StoredAsync();
        Assert.Equal(3, stored.Count);
        Assert.All(stored, row =>
        {
            Assert.Equal("Sintel", row.Title);
            Assert.Equal("movie-sintel", row.EntityId);
        });
    }

    [Fact]
    public async Task A_release_a_person_blocked_is_turned_down_before_it_is_scored()
    {
        await _blocked.BlockAsync(
            new BlockedRelease("b1", BlockedReleaseKeys.ForEveryIndexer("Blocked.Sintel.2010"), "Blocked.Sintel.2010", "Idx", "movies", null, null, "downloadStalled", "It stalled", null, null, null, _time.GetUtcNow()),
            CancellationToken.None);
        var pipeline = Pipeline(
            Candidate("Good.Sintel.2010.1080p", "preferred"),
            Candidate("Blocked.Sintel.2010", "preferred"));

        await pipeline.PlanAsync(Request(mediaType: "tv", entityId: "show-1"));

        var pushed = Assert.Single(_realtime.LiveEventsOf<ReleasesTurnedDownEvent>(LiveEventNames.ReleasesTurnedDown));
        Assert.Equal("series", pushed.EntityType);
        var item = Assert.Single(pushed.Items);
        Assert.Equal("Blocked.Sintel.2010", item.Release);
        Assert.Equal("other", item.Group);
    }

    [Fact]
    public async Task A_search_that_took_everything_or_was_only_a_preview_pushes_and_records_nothing()
    {
        await Pipeline(Candidate("Good.Sintel.2010.1080p", "preferred")).PlanAsync(Request());
        await Pipeline(Candidate("Cam.Sintel.2010", "rejected", gate: "Release name contains a blocked token such as CAM, Telesync, workprint, or screener."))
            .PlanAsync(Request(preview: true));

        Assert.Empty(_realtime.LiveEventsOf<ReleasesTurnedDownEvent>(LiveEventNames.ReleasesTurnedDown));
        Assert.Empty(await StoredAsync());
    }

    [Fact]
    public async Task Turned_down_releases_older_than_a_fortnight_are_cleared_as_new_ones_are_written()
    {
        await Pipeline(Candidate("Old.Sintel.2010", "equivalent")).PlanAsync(Request());
        _time.Advance(TimeSpan.FromDays(15));
        await Pipeline(Candidate("New.Sintel.2010", "equivalent")).PlanAsync(Request());

        Assert.Equal(["New.Sintel.2010"], (await StoredAsync()).Select(row => row.Release).ToArray());
    }

    private sealed class StubPlanner(MediaSearchPlan plan) : IMediaSearchPlanner
    {
        public Task<MediaSearchPlan> BuildPlanAsync(
            string title,
            int? year,
            string mediaType,
            string? currentQuality,
            string? targetQuality,
            IReadOnlyList<LibrarySourceLinkItem> sources,
            IReadOnlyList<CustomFormatItem>? customFormats = null,
            int? seasonNumber = null,
            int? episodeNumber = null,
            IReadOnlyList<string>? allowedQualities = null,
            CancellationToken cancellationToken = default,
            IReadOnlyList<string>? tagNames = null,
            string searchKind = AcquisitionSearchKinds.Automatic,
            DateTimeOffset? availableUtc = null,
            int? currentCustomFormatScore = null,
            string? currentReleaseName = null,
            bool upgradeUntilCutoff = true,
            string? numberingScheme = null,
            int? absoluteNumber = null,
            DateOnly? airDate = null,
            int? sceneSeasonNumber = null,
            int? sceneEpisodeNumber = null,
            PreferenceEvaluationSnapshot? currentPreferenceEvaluation = null,
            ReleasePreferencePlan? preferencePlan = null,
            bool currentFilePresent = false,
            IReadOnlyList<ProfileSizeRule>? sizeRules = null,
            QualityUpgradeStopPolicy? upgradeStop = null,
            ProfileAcquisitionRules? profileAcquisition = null,
            int? runtimeMinutes = null,
            string? imdbId = null,
            ProfileScoreLimits? scoreLimits = null,
            string? genres = null,
            IReadOnlyList<string>? otherTitles = null,
            IReadOnlyList<int>? categoryIds = null)
            => Task.FromResult(plan);
    }
}
