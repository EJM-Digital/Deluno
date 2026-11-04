using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Infrastructure.Resilience;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Tests.Recorded;
using Moq;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1470: a call Deluno sends after the grab that qBittorrent refuses is no longer swallowed. The refusals are qBittorrent 5.2.1's
/// recorded replies (<c>category-unknown</c>: 409 "Incorrect category name"; <c>share-limits</c>: 400 for a missing parameter).
/// Not yet recorded, so not tested: a refused setLocation and a refused setAutoManagement.
/// </summary>
public sealed class TheGrabSaysWhatTheClientRefusedTests
{
    private const string Hash = "d725bf5a29bcf10e3f7d2b8ebbb221b1fe0e783c";
    private const string Release = "Example.Movie.2026.1080p";

    private static DownloadClientGrabRequest Request()
        => new(Release, "magnet:?xt=urn:btih:" + Hash + "&dn=Example", "movies", "movies", "Example Indexer") { KnownInfoHash = Hash };

    private static DownloadClientItem Client(bool shareLimits = false)
        => RecordedClientCalls.Client("qbittorrent", "deluno", "secret") with
        {
            Id = "client-1",
            Name = "qBittorrent",
            ShareRatioLimit = shareLimits ? 1.5 : null
        };

    private static QbittorrentDownloadClient Rig(bool refuseCategory, bool refuseShareLimits)
    {
        var infoCalls = 0;
        var states = new[]
        {
            "sign-in", "version", "settings", "idle", "trackers", "category-moved", "auto-management-set", "location-moved", "share-limits",
            "finished-single-file", "category-unknown"
        };
        var calls = new RecordedClientCalls.Calls("qbittorrent", states.Select(RecordedReplies.Qbittorrent).ToArray(), (request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("torrents/info", StringComparison.Ordinal))
                return RecordedReplies.Json(++infoCalls == 1 ? "[]" : RecordedReplies.Qbittorrent("finished-single-file").Body("torrents/info"));
            if (path.EndsWith("torrents/add", StringComparison.Ordinal)) return RecordedReplies.Json("Ok.");
            if (path.EndsWith("setCategory", StringComparison.Ordinal))
                return refuseCategory ? RecordedReplies.Qbittorrent("category-unknown").Response("torrents/setCategory") : RecordedReplies.Qbittorrent("category-moved").Response("torrents/setCategory");
            if (path.EndsWith("setShareLimits", StringComparison.Ordinal))
                return refuseShareLimits ? RecordedReplies.Qbittorrent("share-limits").Response("torrents/setShareLimits") : RecordedReplies.Qbittorrent("category-moved").Response("torrents/setCategory");
            return null;
        });
        var handler = calls.Build();
        return new QbittorrentDownloadClient(() => handler);
    }

    [Fact]
    public async Task A_refused_category_is_handed_back_with_the_clients_own_words_and_the_grab_still_stands()
    {
        var adapter = Rig(refuseCategory: true, refuseShareLimits: false);

        var result = await adapter.GrabAsync(Client(), Request(), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(Hash, result.ExternalId);
        var refusal = Assert.Single(result.FollowUpRefusals!);
        Assert.Equal("setCategory", refusal.Call);
        Assert.Equal("the movies category", refusal.What);
        Assert.True(refusal.CanStopImport);
        Assert.Equal("Incorrect category name", refusal.Reason);
    }

    [Fact]
    public async Task Refused_share_limits_are_handed_back_but_cannot_stop_the_import()
    {
        var adapter = Rig(refuseCategory: false, refuseShareLimits: true);

        var result = await adapter.GrabAsync(Client(shareLimits: true), Request(), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        var refusal = Assert.Single(result.FollowUpRefusals!);
        Assert.Equal("setShareLimits", refusal.Call);
        Assert.False(refusal.CanStopImport);
        Assert.StartsWith("Missing required parameters", refusal.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Calls_the_client_took_hand_back_no_refusal()
    {
        var adapter = Rig(refuseCategory: false, refuseShareLimits: false);

        var result = await adapter.GrabAsync(Client(shareLimits: true), Request(), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Null(result.FollowUpRefusals);
        Assert.Equal(["setShareLimits", "setCategory", "setAutoManagement"], result.FollowUpCallsSent);
    }

    [Fact]
    public async Task The_grab_service_logs_it_writes_it_to_Activity_and_raises_a_problem_for_the_category()
    {
        var world = new World(Rig(refuseCategory: true, refuseShareLimits: true), shareLimits: true);

        var result = await world.GrabAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(2, world.Activity.Count);
        Assert.Contains("did not take the share limits", world.Activity[0], StringComparison.Ordinal);
        Assert.DoesNotContain("import", world.Activity[0], StringComparison.Ordinal);
        Assert.Contains($"qBittorrent did not take the movies category for {Release}: Incorrect category name", world.Activity[1], StringComparison.Ordinal);
        Assert.Contains("may not be able to import it", world.Activity[1], StringComparison.Ordinal);
        var problem = Assert.Single(world.Health);
        Assert.Equal("grab-follow-up:client-1:setCategory", problem.Id);
        Assert.Equal(HealthCheckStatuses.Problem, problem.Status);
        Assert.False(string.IsNullOrWhiteSpace(problem.Fix));
        Assert.Contains("Incorrect category name", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_later_grab_the_client_takes_clears_the_problem()
    {
        var world = new World(Rig(refuseCategory: false, refuseShareLimits: false), shareLimits: false);
        world.Earlier = new HealthCheckResultItem(
            "grab-follow-up:client-1:setCategory", HealthCheckCategories.Grab, "qBittorrent refused the movies category",
            HealthCheckStatuses.Problem, "x", null, null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

        await world.GrabAsync();

        Assert.Empty(world.Activity);
        var cleared = Assert.Single(world.Health);
        Assert.Equal(HealthCheckStatuses.Ok, cleared.Status);
    }

    [Fact]
    public async Task With_nothing_refused_and_no_earlier_problem_nothing_is_written()
    {
        var world = new World(Rig(refuseCategory: false, refuseShareLimits: false), shareLimits: false);

        await world.GrabAsync();

        Assert.Empty(world.Activity);
        Assert.Empty(world.Health);
    }

    // ---- the save folder and automatic management, from qBittorrent 5.2.1's recorded refusals (location-refused, auto-management-refused) ----

    private static QbittorrentDownloadClient RigAnswering(string path, Func<HttpResponseMessage> reply)
    {
        var infoCalls = 0;
        var states = new[] { "sign-in", "version", "settings", "idle", "trackers", "category-moved", "finished-single-file" };
        var calls = new RecordedClientCalls.Calls("qbittorrent", states.Select(RecordedReplies.Qbittorrent).ToArray(), (request, _) =>
        {
            var requested = request.RequestUri!.AbsolutePath;
            if (requested.EndsWith("torrents/info", StringComparison.Ordinal))
                return RecordedReplies.Json(++infoCalls == 1 ? "[]" : RecordedReplies.Qbittorrent("finished-single-file").Body("torrents/info"));
            if (requested.EndsWith("torrents/add", StringComparison.Ordinal)) return RecordedReplies.Json("Ok.");
            if (requested.EndsWith("setCategory", StringComparison.Ordinal)) return RecordedReplies.Qbittorrent("category-moved").Response("torrents/setCategory");
            return requested.EndsWith(path, StringComparison.Ordinal) ? reply() : null;
        });
        var handler = calls.Build();
        return new QbittorrentDownloadClient(() => handler);
    }

    [Fact]
    public async Task A_save_folder_qBittorrent_cannot_make_is_handed_back_in_its_own_words()
    {
        var recorded = RecordedReplies.Qbittorrent("location-refused");
        var adapter = RigAnswering("setLocation", () => recorded.Exchanges[0].Reply.ToResponse());

        var result = await adapter.GrabAsync(Client() with { Location = @"Z:\no-such-drive\recorded-movies" }, Request(), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        var refusal = Assert.Single(result.FollowUpRefusals!);
        Assert.Equal("setLocation", refusal.Call);
        Assert.Equal(@"the save folder Z:\no-such-drive\recorded-movies", refusal.What);
        Assert.True(refusal.CanStopImport);
        Assert.Equal("Cannot make save path", refusal.Reason);
    }

    [Fact]
    public async Task An_empty_folder_is_refused_400_by_qBittorrent_and_Deluno_never_sends_one()
    {
        var recorded = RecordedReplies.Qbittorrent("location-refused");
        using var answer = recorded.Exchanges[1].Reply.ToResponse();

        var refusals = await DownloadClientHelpers.RefusalOf(answer, "setLocation", "the save folder", true, CancellationToken.None);

        Assert.Equal("Save path cannot be empty", Assert.Single(refusals).Reason);
        // A client with no folder gets automatic management instead, and no setLocation at all.
        var calls = 0;
        var adapter = RigAnswering("setAutoManagement", () => { calls++; return RecordedReplies.Qbittorrent("auto-management-refused").Exchanges[3].Reply.ToResponse(); });
        var result = await adapter.GrabAsync(Client() with { Location = "   " }, Request(), CancellationToken.None);
        Assert.Null(result.FollowUpRefusals);
        Assert.Equal(1, calls);
        Assert.Equal(["setCategory", "setAutoManagement"], result.FollowUpCallsSent);
    }

    [Fact]
    public async Task A_folder_for_a_torrent_qBittorrent_never_had_is_taken_and_says_nothing()
    {
        var adapter = RigAnswering("setLocation", () => RecordedReplies.Qbittorrent("location-refused").Exchanges[2].Reply.ToResponse());

        var result = await adapter.GrabAsync(Client() with { Location = @"/golden/recorded/qbittorrent/save" }, Request(), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Null(result.FollowUpRefusals);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Every_answer_qBittorrent_gives_a_call_with_enable_is_a_taken_one_and_says_nothing(int exchange)
    {
        var adapter = RigAnswering("setAutoManagement", () => RecordedReplies.Qbittorrent("auto-management-refused").Exchanges[exchange].Reply.ToResponse());

        var result = await adapter.GrabAsync(Client(), Request(), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Null(result.FollowUpRefusals);
    }

    [Fact]
    public async Task The_one_automatic_management_call_qBittorrent_refuses_leaves_out_enable_which_Deluno_always_sends()
    {
        var recorded = RecordedReplies.Qbittorrent("auto-management-refused");
        Assert.DoesNotContain("enable", recorded.Exchanges[0].Request.Body!, StringComparison.Ordinal);
        using var answer = recorded.Exchanges[0].Reply.ToResponse();
        Assert.Equal("Missing required parameters: enable", Assert.Single(await DownloadClientHelpers.RefusalOf(answer, "setAutoManagement", "automatic torrent management", false, CancellationToken.None)).Reason);
    }

    // ---- kept on the dispatch so the download's row can say it ----

    [Fact]
    public async Task A_refused_category_is_kept_on_the_dispatch_with_the_clients_words_and_the_fix()
    {
        var world = new World(Rig(refuseCategory: true, refuseShareLimits: true), shareLimits: true);

        await world.GrabAsync(Request() with { DispatchId = "dispatch-1" });

        var (id, json) = Assert.Single(world.Kept);
        Assert.Equal("dispatch-1", id);
        Assert.Contains("Incorrect category name", json, StringComparison.Ordinal);
        var kept = System.Text.Json.Nodes.JsonNode.Parse(json!)!.AsArray().Single()!;
        Assert.Equal("Create the category in qBittorrent or change the library's category, then send it again.", kept["fix"]!.GetValue<string>());
        Assert.DoesNotContain("share limits", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_grab_whose_calls_the_client_took_clears_what_was_kept()
    {
        var world = new World(Rig(refuseCategory: false, refuseShareLimits: false), shareLimits: false);

        await world.GrabAsync(Request() with { DispatchId = "dispatch-1" });

        var (_, json) = Assert.Single(world.Kept);
        Assert.Null(json);
    }

    [Fact]
    public async Task A_grab_with_no_dispatch_keeps_nothing()
    {
        var world = new World(Rig(refuseCategory: true, refuseShareLimits: false), shareLimits: false);

        await world.GrabAsync();

        Assert.Empty(world.Kept);
    }

    private sealed class World
    {
        private readonly DownloadClientGrabService service;

        public HealthCheckResultItem? Earlier { get; set; }

        public List<string> Activity { get; } = [];

        public List<HealthCheckOutcome> Health { get; } = [];

        public List<(string Id, string? Json)> Kept { get; } = [];

        public World(QbittorrentDownloadClient adapter, bool shareLimits)
        {
            var client = Client(shareLimits);
            var connections = new Mock<IConnectionsRepository>();
            connections.Setup(instance => instance.ListDownloadClientsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([client]);
            connections.Setup(instance => instance.ListIndexersAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
            connections.Setup(instance => instance.ListDownloadClientPathMappingsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
            var registry = new Mock<IDownloadClientRegistry>();
            IDownloadClient captured = adapter;
            registry.Setup(instance => instance.TryGet(It.IsAny<string?>(), out captured)).Returns(true);
            var dispatches = new Mock<IDownloadDispatchesRepository>();
            dispatches.Setup(instance => instance.FindDispatchesByClientItemIdsAsync(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
            dispatches.Setup(instance => instance.SetFollowUpRefusalsAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .Returns((string id, string? json, CancellationToken _) =>
                {
                    Kept.Add((id, json));
                    return Task.CompletedTask;
                });
            var activity = new Mock<IActivityFeedRepository>();
            activity.Setup(instance => instance.RecordActivityAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string category, string message, string? details, string? job, string? entityType, string? entityId, CancellationToken _) =>
                {
                    Assert.Equal("download.follow-up.refused", category);
                    Activity.Add(message);
                    return new ActivityEventItem(Guid.NewGuid().ToString("n"), category, message, details, job, entityType, entityId, DateTimeOffset.UnixEpoch);
                });
            var health = new Mock<IHealthCheckRepository>();
            health.Setup(instance => instance.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((string id, CancellationToken _) => Earlier is not null && Earlier.Id == id ? Earlier : null);
            health.Setup(instance => instance.UpsertResultsAsync(It.IsAny<IReadOnlyList<HealthCheckOutcome>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyList<HealthCheckOutcome> outcomes, CancellationToken _) =>
                {
                    Health.AddRange(outcomes);
                    return [];
                });

            service = new DownloadClientGrabService(
                connections.Object,
                registry.Object,
                new PassThrough(),
                dispatches.Object,
                Mock.Of<IRealtimeEventPublisher>(),
                healthCheckRepository: health.Object,
                activityFeedRepository: activity.Object);
        }

        public Task<DownloadClientGrabResult> GrabAsync() => service.GrabAsync("client-1", Request(), CancellationToken.None);

        public Task<DownloadClientGrabResult> GrabAsync(DownloadClientGrabRequest request) => service.GrabAsync("client-1", request, CancellationToken.None);
    }

    private sealed class PassThrough : IIntegrationResiliencePolicy
    {
        public async Task<IntegrationResilienceResult<T>> ExecuteAsync<T>(
            IntegrationResilienceRequest request,
            Func<CancellationToken, Task<T>> operation,
            Func<T, IntegrationResilienceOutcome> classifyResult,
            CancellationToken cancellationToken)
            => new(await operation(cancellationToken), false, false, 1, null, null);

        public bool IsCircuitOpen(string key, out DateTimeOffset retryAfterUtc)
        {
            retryAfterUtc = default;
            return false;
        }

        public Task CloseCircuitsAsync(string serviceType, string serviceId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
