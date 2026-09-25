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
