using Deluno.Companions;
using Deluno.Connections.Contracts;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Contracts;
using Deluno.Movies.Contracts;
using Deluno.Platform.Contracts;
using Deluno.Series.Contracts;
using Deluno.Worker.Attention;
using Deluno.Worker.Dashboard;

namespace Deluno.Worker.Tests.Dashboard;

/// <summary>The one definition of what needs a person (#1185): every kind of row, what is left out, and the number.</summary>
public sealed class AttentionSummaryBuilderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 3, 0, 0, TimeSpan.Zero);

    private static DashboardSummary Summary(
        IReadOnlyList<DashboardClientRow>? clients = null,
        bool outage = false,
        bool queueUnreadable = false,
        int collectionsFailing = 0,
        int requestServersFailing = 0,
        bool updateAvailable = false,
        IReadOnlyList<DashboardLane>? lanes = null)
    {
        var empty = new Dictionary<string, int>();
        return new DashboardSummary(
            Now, 0, [],
            Library: new DashboardLibrarySection(Now, null!, null!, 0, [], null!, new DashboardCollections(1, 1, 0, 0, collectionsFailing, null)),
            Downloads: new DashboardDownloadsSection(Now, true, Now, 0, 0, 0, 0, 0, 0, 0, 0, clients ?? [], 0, 0, new DashboardLeftovers(true, 2, 0, Now), 0),
            Pipeline: null,
            Imports: new DashboardImportsSection(Now, 0, empty, 0, 0),
            Automation: new DashboardAutomationSection(Now, true, new DashboardQueuePauseGate(true, 5, false, false, 0, 0, queueUnreadable, null), [], null, []),
            Indexers: null, Processors: null, Retention: null, Subtitles: null,
            Requests: new DashboardRequestsSection(Now, 1, 0, requestServersFailing, empty, 0, empty, 0),
            Blocklist: null,
            Health: new DashboardHealthSection(Now, 0, 0, 3, null, []),
            Storage: null,
            System: new DashboardSystemSection(Now, "1", updateAvailable, null, 0, null, new DashboardJobCounts(0, 0, 0), lanes ?? [], null, null, false, outage, null),
            Activity: null);
    }

    private static AttentionSources Sources(
        DashboardSummary? summary = null,
        IReadOnlyList<DownloadQueueItem>? queue = null,
        IReadOnlyList<MovieImportRecoveryCase>? movieCases = null,
        IReadOnlyList<SeriesImportRecoveryCase>? seriesCases = null,
        IReadOnlyList<JobQueueItem>? jobs = null,
        IReadOnlyList<ProcessorHandoffItem>? handoffs = null,
        IReadOnlyList<DownloadDispatchItem>? dispatches = null,
        IReadOnlyList<IndexerItem>? indexers = null,
        IReadOnlyList<ProcessorConnectionItem>? processors = null,
        IReadOnlyList<HealthCheckResultItem>? health = null,
        IReadOnlyList<AttentionEndpoints.NoReleaseRow>? noRelease = null,
        IReadOnlyList<ConnectionUpdateItem>? updates = null)
        => new(
            summary ?? Summary(),
            new DownloadTelemetryOverview(
                new DownloadTelemetrySummary(0, 0, 0, 0, 0, 0, 0),
                [new DownloadClientTelemetrySnapshot("c1", "Client", "qbittorrent", null, "healthy", null,
                    new DownloadClientTelemetryCapabilities(true, true, true, true, true, true, "none"),
                    new DownloadTelemetrySummary(0, 0, 0, 0, 0, 0, 0), queue ?? [], [], Now)],
                Now),
            movieCases ?? [], seriesCases ?? [], jobs ?? [], handoffs ?? [], dispatches ?? [],
            indexers ?? [], processors ?? [], health ?? [], noRelease ?? [], updates ?? [], Now);

    private static DownloadQueueItem Stalled(string id = "q1")
        => new(id, "c1", "Client", "qbittorrent", "movies", "Pulp Fiction", "Pulp.Fiction.1994", "movies",
            DownloadQueueStatuses.Stalled, 10, 0, 0, 1_000, 100, 0, "Indexer", null, Now);

    private static MovieImportRecoveryCase MovieCase(string id = "r1", string? details = null)
        => new(id, "Pulp Fiction", "unmatched", "open", "Deluno could not match the file.", "Match it by hand", details, Now, null);

    private static SeriesImportRecoveryCase SeriesCase(string id = "r2")
        => new(id, "The Simpsons", "importFailed", "open", "Named files Deluno cannot find.", "Retry", null, Now, null);

    private static JobQueueItem Job(string id, string type, string? entity = null, string status = "dead-letter", DateTimeOffset? next = null, int attempts = 5, int max = 5, string? payload = null)
        => new(id, type, "system", status, payload, attempts, Now, Now, null, Now, null, null, "It failed.", null, entity, null, null, max, Now, next);

    private static ProcessorHandoffItem Handoff(string id = "h1", string status = "failed", string client = "c1", string item = "q9", string? message = "Rejected.")
        => new(id, "lib", "movies", client, item, "Oppenheimer.2023", "/downloads/x", "Weir", status, null, null, message, Now, Now);

    private static IndexerItem Indexer(string id, string health, bool enabled = true)
        => new(id, $"Indexer {id}", "usenet", "public", "http://example.test", null, 25, "2000", "", "all", enabled, health, null, null, null, null, 0, null, null, Now, Now);

    private static ProcessorConnectionItem Processor(string id, string health, bool enabled = true)
        => new(id, $"Processor {id}", "weir", "http://example.test/hook", "X-Key", null, enabled, health, null, null, Now, Now);

    private static HealthCheckResultItem Check(string id, string status = HealthCheckStatuses.Problem, DateTimeOffset? snoozedUntil = null, string category = "system")
        => new(id, category, $"Check {id}", status, "Something is wrong.", null, null, null, Now, Now.AddHours(-2), snoozedUntil);

    private static ConnectionUpdateItem Notice(string id, bool notice = true)
        => new("downloadClient", id, "qBittorrent", "qbittorrent", "qBittorrent", "4.6.7", "5.0.2", null, null, notice, "updateNow", "5.0.2", "A newer version is out.");

    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void Nothing_wrong_is_nothing_to_count()
    {
        var result = AttentionSummaryBuilder.Build(Sources());

        Assert.Equal(0, result.Count);
        Assert.Empty(result.Rows);
        Assert.Empty(result.Notices);
    }

    [Fact]
    public void Every_kind_of_row_is_built_and_the_count_is_their_sum()
    {
        var summary = Summary(
            clients: [new DashboardClientRow("cl1", "SABnzbd", "usenet", true, null, null, 2, null, "healthy", 0, 0)],
            outage: true, queueUnreadable: true, collectionsFailing: 1, requestServersFailing: 2, updateAvailable: true,
            lanes: [new DashboardLane("Searching", "failing", 0, null), new DashboardLane("Imports", "failing", 0, null)]);

        var result = AttentionSummaryBuilder.Build(Sources(
            summary,
            queue: [Stalled()],
            movieCases: [MovieCase()],
            seriesCases: [SeriesCase()],
            jobs:
            [
                Job("j-import", "filesystem.import.execute", "movie-1"),
                Job("j-bg", "library.refresh"),
                Job("j-title", "search.run", "movie-2"),
                Job("j-retry", "library.refresh", status: "failed", next: Now.AddMinutes(5), attempts: 1)
            ],
            handoffs: [Handoff()],
            indexers: [Indexer("i1", "unreachable"), Indexer("i2", "healthy"), Indexer("i3", "unreachable", enabled: false)],
            processors: [Processor("p1", "degraded"), Processor("p2", "healthy")],
            health: [Check("machine:reboot"), Check("system:backup")],
            noRelease:
            [
                new AttentionEndpoints.NoReleaseRow("movie", "m1", "Heat", 1995, "lib", Now, Now, null),
                new AttentionEndpoints.NoReleaseRow("series", "s1", "Severance", null, "lib", Now, null, null)
            ],
            updates: [Notice("dc1"), Notice("dc2", notice: false)]));

        var ids = result.Rows.Select(row => row.Id).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(
            new[]
            {
                "client:cl1", "collections", "failed-jobs", "failed-title-jobs", "handoff:h1", "health:machine:reboot",
                "health:system:backup", "import:j-import", "indexer:i1", "lanes", "no-release:movie:m1", "no-release:series:s1",
                "outage", "processor:p1", "queue-gate", "recovery:movie:r1", "recovery:series:r2", "requests", "stuck:c1:q1"
            }.Order(StringComparer.Ordinal).ToArray(),
            ids);

        // The two failing workers are one row that stands for two.
        Assert.Equal(2, result.Rows.Single(row => row.Id == "lanes").Count);
        Assert.Equal(result.Rows.Sum(row => row.Count), result.Count);
        Assert.Equal(20, result.Count);

        // A newer Deluno and a newer connected app are listed in their own group and are in no number.
        Assert.Equal(new[] { "connection-update:downloadClient:dc1", "update" }, result.Notices.Select(row => row.Id).Order(StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(result.Rows, row => row.Kind is "update" or "connection-update");
        Assert.Equal(2, result.Leftovers);
        Assert.Equal(3, result.Suggestions);
    }

    [Fact]
    public void A_client_with_warnings_opens_its_own_drawer_where_they_are_cleared()
    {
        var result = AttentionSummaryBuilder.Build(Sources(Summary(clients:
        [
            new DashboardClientRow("cl1", "SABnzbd", "usenet", true, null, null, 2, null, "healthy", 0, 0),
            new DashboardClientRow("cl2", "NZBGet", "usenet", false, null, null, 0, null, "failing", 0, 0)
        ])));

        var warnings = result.Rows.Single(row => row.Id == "client:cl1");
        Assert.Equal("Clear warnings", warnings.Action);
        Assert.Equal("/indexers/download-clients?connection=cl1", warnings.To);
        Assert.Equal("Open System", result.Rows.Single(row => row.Id == "client:cl2").Action);
    }

    [Fact]
    public void Rows_say_which_section_they_are_in_and_worst_comes_first()
    {
        var result = AttentionSummaryBuilder.Build(Sources(
            Summary(outage: true),
            queue: [Stalled()],
            noRelease: [new AttentionEndpoints.NoReleaseRow("movie", "m1", "Heat", 1995, "lib", Now, Now, null)]));

        Assert.Equal("bad", result.Rows[0].Tone);
        Assert.All(result.Rows.Where(row => row.Kind is "stuck" or "no-release"), row => Assert.Equal("media", row.Section));
        Assert.Equal("deluno", result.Rows.Single(row => row.Id == "outage").Section);
        Assert.Equal("Heat (1995)", result.Rows.Single(row => row.Kind == "no-release").Title);
    }

    [Fact]
    public void A_health_problem_about_a_connection_with_its_own_row_is_that_row_and_not_a_second_one()
    {
        var result = AttentionSummaryBuilder.Build(Sources(
            Summary(clients: [new DashboardClientRow("cl1", "SABnzbd", "usenet", false, null, null, 0, null, "failing", 0, 0)]),
            indexers: [Indexer("i1", "unreachable")],
            health:
            [
                Check("download-client:cl1", category: "download-client"),
                Check("indexer:i1", category: "indexer"),
                // NZBGet answers, so it has no client row of its own; its Health problem is listed.
                Check("download-client:nzbget", category: "download-client")
            ]));

        Assert.Equal(
            new[] { "client:cl1", "health:download-client:nzbget", "indexer:i1" },
            result.Rows.Select(row => row.Id).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void A_snoozed_or_cleared_health_problem_is_not_counted()
    {
        var result = AttentionSummaryBuilder.Build(Sources(health:
        [
            Check("machine:reboot", snoozedUntil: Now.AddHours(1)),
            Check("machine:sleep", HealthCheckStatuses.Ok),
            Check("machine:drive:c", snoozedUntil: Now.AddHours(-1))
        ]));

        Assert.Equal(["health:machine:drive:c"], result.Rows.Select(row => row.Id));
    }

    [Fact]
    public void One_failure_is_one_row_whichever_record_names_it()
    {
        // A failed hand-off, the dead-lettered import job and the case behind the same download are one problem.
        var dispatch = new DownloadDispatchItem(
            "d1", "lib", "movies", "movie", "m1", "Oppenheimer.2023", "Indexer", "c1", "Client", "failed", null, Now,
            null, null, null, null, null, null, null, "q9", null, "failed", null, null, null, null, "import failed", null);
        var handoff = Handoff(item: "q9") with { ImportJobId = "j1" };
        var caseForSameDownload = MovieCase("r1", """{"DispatchId":"d1"}""");
        var job = Job("j1", "filesystem.import.execute", "m1", payload: """{"DispatchId":"d1"}""");

        var result = AttentionSummaryBuilder.Build(Sources(
            queue: [Stalled("q9")],
            movieCases: [caseForSameDownload],
            jobs: [job],
            handoffs: [handoff],
            dispatches: [dispatch]));

        Assert.Equal(["handoff:h1"], result.Rows.Select(row => row.Id));
        Assert.Equal(1, result.Count);
    }

    [Fact]
    public void A_download_that_was_imported_since_has_nothing_left_to_ask()
    {
        var imported = new DownloadDispatchItem(
            "d2", "lib", "movies", "movie", "m1", "Heat.1995", "Indexer", "c1", "Client", "completed", null, Now,
            null, null, null, null, null, null, null, "q1", null, "imported", null, null, null, null, null, null);

        var result = AttentionSummaryBuilder.Build(Sources(queue: [Stalled("q1")], dispatches: [imported]));

        Assert.Equal(0, result.Count);
    }

    [Fact]
    public void A_dismissed_hand_off_and_a_job_that_will_be_tried_again_need_nobody()
    {
        var result = AttentionSummaryBuilder.Build(Sources(
            jobs: [Job("j1", "filesystem.import.execute", "m1", status: "failed", next: Now.AddMinutes(3), attempts: 1)],
            handoffs: [Handoff(status: "dismissed")]));

        Assert.Equal(0, result.Count);
    }

    [Fact]
    public void Update_notices_never_change_the_number()
    {
        var without = AttentionSummaryBuilder.Build(Sources(Summary(outage: true)));
        var with = AttentionSummaryBuilder.Build(Sources(Summary(outage: true, updateAvailable: true), updates: [Notice("a"), Notice("b"), Notice("c")]));

        Assert.Equal(1, without.Count);
        Assert.Equal(without.Count, with.Count);
        Assert.Equal(4, with.Notices.Count);
    }

    [Theory]
    [InlineData("", "The import failed. If the title is already where it belongs, dismiss this; otherwise try again.")]
    [InlineData("Error: timed out.", "The import timed out. If the title is already where it belongs, dismiss this; otherwise try again.")]
    [InlineData("Disk is full", "Disk is full")]
    public void A_bare_failure_word_is_dressed_with_the_step(string raw, string expected)
        => Assert.Equal(expected, AttentionSummaryBuilder.PlainFailure(raw, "The import"));
}
