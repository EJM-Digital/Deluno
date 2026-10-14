using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Integrations.MediaServers;
using Deluno.Retention;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Deluno.Worker.Tests.Retention;

/// <summary>
/// #521: each server's Leaving soon collection holds what every enabled rule
/// reading it has marked; a failure is recorded against the server and tried
/// again; nothing is touched on a server with nothing leaving.
/// </summary>
public sealed class RetentionCollectionServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-17T00:00:00Z");
    private readonly InMemoryRetentionRepository _store = new();
    private readonly RecordingClient _jellyfin = new(MediaServerKinds.Jellyfin);
    private readonly RecordingClient _plex = new(MediaServerKinds.Plex);
    private readonly List<MediaServerConnection> _servers =
    [
        Server("jf", MediaServerKinds.Jellyfin),
        Server("px", MediaServerKinds.Plex) with { LeavingSoonOnHome = true }
    ];

    [Fact]
    public async Task Every_rules_marks_on_a_server_go_in_its_collection_once()
    {
        _store.Add(Rule("r1", RetentionLevels.Movie, "jf", "px"));
        _store.Add(Rule("r2", RetentionLevels.Movie, "jf"));
        await Mark("r1", "m1", ("jf", "j1", null), ("px", "p1", "1"));
        await Mark("r2", "m1", ("jf", "j1", null));
        await Mark("r2", "m2", ("jf", "j2", null));

        await Service().SyncAsync(CancellationToken.None);

        var jellyfin = Assert.Single(_jellyfin.Requests);
        Assert.Equal(("movies", "Leaving soon"), (jellyfin.MediaType, jellyfin.Name));
        Assert.Equal(["j1", "j2"], jellyfin.Members.Select(member => member.ItemId).Order());
        var plex = Assert.Single(_plex.Requests);
        Assert.Equal([("p1", "1")], plex.Members.Select(member => (member.ItemId, member.SectionId)));
        Assert.True(plex.PromoteToHome);
        Assert.Null(_store.Collections[("jf", "movies")].Problem);
    }

    [Fact]
    public async Task A_rule_that_keeps_its_list_in_Deluno_only_never_creates_anything_on_a_server()
    {
        _store.Add(Rule("r1", RetentionLevels.Movie, "jf", "px") with { KeepInDelunoOnly = true });
        await Mark("r1", "m1", ("jf", "j1", null), ("px", "p1", "1"));

        await Service().SyncAsync(CancellationToken.None);

        Assert.Empty(_jellyfin.Requests);
        Assert.Empty(_plex.Requests);
        Assert.Empty(_store.Collections);
        // Its marks are still Deluno's own: reviewing, keeping and postponing them is untouched.
        Assert.Single(await _store.ListMarksAsync("r1", CancellationToken.None));
    }

    [Fact]
    public async Task Turning_keep_in_Deluno_only_on_takes_the_titles_off_the_server_and_another_rules_titles_stay()
    {
        _store.Add(Rule("r1", RetentionLevels.Movie, "jf"));
        _store.Add(Rule("r2", RetentionLevels.Movie, "jf"));
        await Mark("r1", "m1", ("jf", "j1", null));
        await Mark("r2", "m2", ("jf", "j2", null));
        await Service().SyncAsync(CancellationToken.None);
        Assert.Equal(["j1", "j2"], _jellyfin.Requests[0].Members.Select(member => member.ItemId).Order());

        _store.Add(Rule("r1", RetentionLevels.Movie, "jf") with { KeepInDelunoOnly = true });
        await Service().SyncAsync(CancellationToken.None);

        Assert.Equal(["j2"], _jellyfin.Requests[1].Members.Select(member => member.ItemId));
    }

    [Fact]
    public async Task A_rule_that_does_not_remove_titles_has_nothing_leaving_to_show_on_a_server()
    {
        _store.Add(Rule("r1", RetentionLevels.Movie, "jf") with { ExpiryAction = RetentionExpiryActions.Unmonitor });
        await Mark("r1", "m1", ("jf", "j1", null));

        await Service().SyncAsync(CancellationToken.None);

        Assert.Empty(_jellyfin.Requests);
    }

    [Fact]
    public async Task A_server_with_nothing_leaving_is_not_touched()
    {
        _store.Add(Rule("r1", RetentionLevels.Movie, "jf"));

        await Service().SyncAsync(CancellationToken.None);

        Assert.Empty(_jellyfin.Requests);
        Assert.Empty(_store.Collections);
    }

    [Fact]
    public async Task A_disabled_rules_marks_leave_and_the_emptied_collection_is_asked_to_go()
    {
        _store.Add(Rule("r1", RetentionLevels.Movie, "jf"));
        await Mark("r1", "m1", ("jf", "j1", null));
        await Service().SyncAsync(CancellationToken.None);

        _store.Add(Rule("r1", RetentionLevels.Movie, "jf") with { IsEnabled = false });
        await Service().SyncAsync(CancellationToken.None);

        Assert.Equal(2, _jellyfin.Requests.Count);
        Assert.Empty(_jellyfin.Requests[1].Members);
        Assert.Equal(0, _store.Collections[("jf", "movies")].Members);

        // Once it is gone, nothing more is sent.
        await Service().SyncAsync(CancellationToken.None);
        Assert.Equal(2, _jellyfin.Requests.Count);
    }

    [Fact]
    public async Task An_unreachable_server_is_recorded_and_caught_up_next_time()
    {
        _store.Add(Rule("r1", RetentionLevels.Movie, "jf"));
        await Mark("r1", "m1", ("jf", "j1", null));
        _jellyfin.Answer = new MediaServerAnswer<MediaServerCollectionSync>.Unreachable("Deluno could not reach Rig lab Jellyfin.");

        await Service().SyncAsync(CancellationToken.None);

        var failed = _store.Collections[("jf", "movies")];
        Assert.Equal("Deluno could not reach Rig lab Jellyfin.", failed.Problem);
        Assert.Null(failed.SyncedUtc);

        _jellyfin.Answer = null;
        await Service().SyncAsync(CancellationToken.None);

        var caughtUp = _store.Collections[("jf", "movies")];
        Assert.Null(caughtUp.Problem);
        Assert.Equal(Now, caughtUp.SyncedUtc);
    }

    [Fact]
    public async Task A_client_that_throws_is_a_recorded_failure_not_a_crash()
    {
        _store.Add(Rule("r1", RetentionLevels.Movie, "jf"));
        await Mark("r1", "m1", ("jf", "j1", null));
        _jellyfin.Throw = true;

        await Service().TrySyncAsync(CancellationToken.None);

        Assert.Equal("Deluno could not update the Leaving soon collection on Rig jellyfin.", _store.Collections[("jf", "movies")].Problem);
    }

    // ---- #1301: the same service with the real Jellyfin client, answered by Jellyfin 12.2.0's recorded replies ----

    private sealed class RecordedFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>A Jellyfin that holds (or does not hold) the Leaving soon collection, answering with its recorded replies and noting every call.</summary>
    private HttpMessageHandler RecordedJellyfin(List<string> calls, bool collectionExists)
        => Deluno.Tests.Recorded.RecordedReplies.Handler("jellyfin", [], (request, body) =>
        {
            calls.Add($"{request.Method} {request.RequestUri!.PathAndQuery}");
            var recorded = Deluno.Tests.Recorded.RecordedReplies.Jellyfin("collection-sync");
            var path = request.RequestUri.AbsolutePath;
            var query = request.RequestUri.Query;
            if (path == "/Users") return Deluno.Tests.Recorded.RecordedReplies.Jellyfin("libraries").Exchanges[1].Reply.ToResponse();
            if (path == "/Items" && query.Contains("IncludeItemTypes=BoxSet"))
            {
                return collectionExists
                    ? Deluno.Tests.Recorded.RecordedReplies.Json(recorded.Body("IncludeItemTypes=BoxSet", ("Items.0.Name", "Leaving soon")))
                    : Deluno.Tests.Recorded.RecordedReplies.Jellyfin("library-empty").Exchanges[2].Reply.ToResponse();
            }

            if (path == "/Collections" && request.Method == HttpMethod.Post) return recorded.Exchanges[0].Reply.ToResponse();
            if (path == "/Items" && query.Contains("Ids=")) return recorded.Exchanges[1].Reply.ToResponse();
            if (path.StartsWith("/Items/", StringComparison.Ordinal) && request.Method == HttpMethod.Post) return recorded.Exchanges[2].Reply.ToResponse();
            if (path.Contains("/FavoriteItems/", StringComparison.Ordinal)) return recorded.Exchanges[3].Reply.ToResponse();
            if (path.EndsWith("/Items", StringComparison.Ordinal) && query.Contains("ParentId")) return recorded.Exchanges[7].Reply.ToResponse();
            if (path.StartsWith("/Collections/", StringComparison.Ordinal)) return recorded.Exchanges[request.Method == HttpMethod.Delete ? 5 : 4].Reply.ToResponse();
            if (path.StartsWith("/Items/", StringComparison.Ordinal) && request.Method == HttpMethod.Delete)
                return Deluno.Tests.Recorded.RecordedReplies.Jellyfin("collection-deleted").Exchanges[0].Reply.ToResponse();
            return null;
        });

    private RetentionCollectionService RealJellyfinService(HttpMessageHandler handler)
    {
        var servers = new Mock<IMediaServerRepository>();
        servers.Setup(repo => repo.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync([Server("jf", MediaServerKinds.Jellyfin)]);
        return new RetentionCollectionService(
            _store,
            servers.Object,
            new MediaServerClientRegistry([new JellyfinMediaServerClient(new RecordedFactory(handler))]),
            new FixedClock(Now),
            NullLogger<RetentionCollectionService>.Instance);
    }

    [Fact]
    public async Task What_a_rule_marks_goes_into_a_real_jellyfins_collection_by_the_calls_it_recorded_taking()
    {
        _store.Add(Rule("r1", RetentionLevels.Movie, "jf"));
        await Mark("r1", "m1", ("jf", "3df4dba3fab3ef172511446b46f3e8a0", null));
        var calls = new List<string>();

        await RealJellyfinService(RecordedJellyfin(calls, collectionExists: false)).SyncAsync(CancellationToken.None);

        Assert.Contains(calls, call => call == "POST /Collections?Name=Leaving%20soon&Ids=3df4dba3fab3ef172511446b46f3e8a0");
        var synced = _store.Collections[("jf", "movies")];
        Assert.Null(synced.Problem);
        Assert.Equal(1, synced.Members);
    }

    [Fact]
    public async Task Turning_the_rule_off_asks_a_real_jellyfin_to_delete_its_emptied_collection_and_nothing_else()
    {
        _store.Add(Rule("r1", RetentionLevels.Movie, "jf"));
        await Mark("r1", "m1", ("jf", "3df4dba3fab3ef172511446b46f3e8a0", null));
        var calls = new List<string>();
        await RealJellyfinService(RecordedJellyfin(calls, collectionExists: false)).SyncAsync(CancellationToken.None);
        calls.Clear();

        _store.Add(Rule("r1", RetentionLevels.Movie, "jf") with { IsEnabled = false });
        await RealJellyfinService(RecordedJellyfin(calls, collectionExists: true)).SyncAsync(CancellationToken.None);

        Assert.Equal(["DELETE /Items/18da5b93435bf32b1375cc6efe0ca347"], calls.Where(call => call.StartsWith("DELETE", StringComparison.Ordinal)));
        Assert.Equal(0, _store.Collections[("jf", "movies")].Members);
        Assert.Null(_store.Collections[("jf", "movies")].Problem);
    }

    [Fact]
    public async Task A_jellyfin_that_does_not_answer_is_recorded_against_the_server_and_caught_up_when_it_does()
    {
        _store.Add(Rule("r1", RetentionLevels.Movie, "jf"));
        await Mark("r1", "m1", ("jf", "3df4dba3fab3ef172511446b46f3e8a0", null));
        var down = Deluno.Tests.Recorded.RecordedReplies.Jellyfin("not-answering").NotAnswering();

        await RealJellyfinService(down).SyncAsync(CancellationToken.None);

        var failed = _store.Collections[("jf", "movies")];
        Assert.NotNull(failed.Problem);
        Assert.Null(failed.SyncedUtc);

        await RealJellyfinService(RecordedJellyfin([], collectionExists: false)).SyncAsync(CancellationToken.None);

        var caughtUp = _store.Collections[("jf", "movies")];
        Assert.Null(caughtUp.Problem);
        Assert.Equal(Now, caughtUp.SyncedUtc);
    }

    private RetentionCollectionService Service()
    {
        var servers = new Mock<IMediaServerRepository>();
        servers.Setup(repo => repo.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(_servers);
        return new RetentionCollectionService(
            _store,
            servers.Object,
            new MediaServerClientRegistry([_jellyfin, _plex]),
            new FixedClock(Now),
            NullLogger<RetentionCollectionService>.Instance);
    }

    private Task Mark(string ruleId, string entityId, params (string Server, string Item, string? Section)[] items)
        => _store.UpsertMarkAsync(
            new RetentionMark(ruleId, ruleId, entityId, RetentionLevels.Movie, null, null, [], entityId, 2000, 1, ["why"], Now, Now.AddDays(14), RetentionMarkStates.Leaving, null, Now)
            {
                ServerItems = items.Select(item => new RetentionServerItem(item.Server, item.Item, MediaServerItemLevels.Movie, item.Section)).ToArray()
            },
            CancellationToken.None);

    private static RetentionRule Rule(string id, string level, params string[] servers)
        => new(id, id, level, true, [], servers, false, 14, RetentionRemovalModes.Automatic, 8, null, Now, Now);

    private static MediaServerConnection Server(string id, string kind)
        => new(id, kind, "Rig " + kind, "http://127.0.0.1:8096", "token", true, "healthy", null, null, null, null, null, Now, Now);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingClient(string kind) : IMediaServerClient
    {
        public string Kind => kind;

        public List<MediaServerCollectionRequest> Requests { get; } = [];

        public MediaServerAnswer<MediaServerCollectionSync>? Answer { get; set; }

        public bool Throw { get; set; }

        public Task<MediaServerTestResult> TestAsync(MediaServerConnection connection, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<MediaServerAnswer<MediaServerLibrary>> ListItemsAsync(MediaServerConnection connection, string mediaType, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<MediaServerAnswer<MediaServerRemoval>> RemoveAsync(MediaServerConnection connection, MediaServerItem item, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<MediaServerAnswer<MediaServerRefresh>> RefreshAsync(MediaServerConnection connection, string mediaType, IReadOnlyList<MediaServerPathChange> changes, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<MediaServerAnswer<MediaServerPathMappingTest>> TestPathAsync(MediaServerConnection connection, string serverPath, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<MediaServerAnswer<MediaServerCollectionSync>> SyncCollectionAsync(MediaServerConnection connection, MediaServerCollectionRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (Throw) throw new HttpRequestException("boom");
            return Task.FromResult(Answer ?? new MediaServerAnswer<MediaServerCollectionSync>.Reported(new MediaServerCollectionSync(request.Members.Count, 0, request.Members.Count, 0, request.Members.Count == 0)));
        }
    }
}
