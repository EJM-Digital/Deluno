using Deluno.Connections.Contracts;
using Deluno.Platform.Contracts;
using Deluno.Recovery.Policies;
using Deluno.Recovery.Services;

namespace Deluno.Persistence.Tests.Integrations;

public sealed class SharingReclaimServiceTests
{
    private sealed class RecordingGateway(bool succeeds = true, string message = "Removed.") : IDownloadClientActionGateway
    {
        public List<(string ClientId, string QueueItemId)> Calls { get; } = [];

        public Task<DownloadClientRemovalResult> RemoveWithDataAsync(string clientId, string queueItemId, CancellationToken cancellationToken)
        {
            Calls.Add((clientId, queueItemId));
            return Task.FromResult(new DownloadClientRemovalResult(succeeds, message));
        }
    }

    private static SharingReclaimCandidate Candidate(
        double? ratio = null,
        int? seedingMinutes = 0,
        string protocol = "qbittorrent")
        => new("hash-1", "client-1", "qBittorrent", "Sintel (2010)", protocol, ratio, seedingMinutes);

    private static IndexerItem Source(
        string? mode = null,
        int? forHours = null,
        double? untilRatio = null,
        string? stuck = null,
        int? stuckDays = null)
        => new(
            Id: "src-1", Name: "A tracker", Protocol: "torznab", Privacy: "private",
            BaseUrl: "http://localhost/api", ApiKey: null, Priority: 1, Categories: "2000",
            Tags: "", MediaScope: "both", IsEnabled: true, HealthStatus: "healthy",
            LastHealthMessage: null, LastHealthFailureCategory: null, LastHealthLatencyMs: null,
            LastHealthTestUtc: null, ConsecutiveFailures: 0, RateLimitedUntilUtc: null,
            DisabledReason: null, CreatedUtc: DateTimeOffset.UnixEpoch, UpdatedUtc: DateTimeOffset.UnixEpoch)
        {
            SharingMode = mode,
            SharingForHours = forHours,
            SharingUntilRatio = untilRatio,
            SharingStuckAction = stuck,
            SharingStuckAfterDays = stuckDays
        };

    // ── Which rule applies ────────────────────────────────────────────────

    [Fact]
    public void A_source_with_no_rule_of_its_own_uses_the_global_one()
    {
        var global = new SharingPolicy(SharingPolicy.ModeShareThenTidy, 72, null, SharingPolicy.StuckGiveUp, 14);

        Assert.Same(global, SharingReclaimService.EffectivePolicyFor(global, Source()));
        Assert.Same(global, SharingReclaimService.EffectivePolicyFor(global, null));
    }

    [Fact]
    public void A_source_override_layers_over_the_global_rule()
    {
        // The private tracker wants a ratio; everything else about the rule —
        // mode, the give-up behaviour, the cap — still comes from the global.
        var global = new SharingPolicy(SharingPolicy.ModeShareThenTidy, 72, null, SharingPolicy.StuckGiveUp, 14);

        var effective = SharingReclaimService.EffectivePolicyFor(global, Source(untilRatio: 2.0));

        Assert.Equal(2.0, effective.UntilRatio);
        Assert.Equal(72, effective.ForHours);
        Assert.Equal(SharingPolicy.ModeShareThenTidy, effective.Mode);
        Assert.Equal(14, effective.StuckAfterDays);
    }

    // ── Acting on the decision ────────────────────────────────────────────

    [Fact]
    public async Task Reclaims_through_the_client_once_the_target_is_met()
    {
        var gateway = new RecordingGateway();
        var service = new SharingReclaimService(gateway);

        var outcome = await service.ReconcileAsync(
            Candidate(seedingMinutes: 72 * 60),
            new SharingPolicy(SharingPolicy.ModeShareThenTidy, 72, null, SharingPolicy.StuckGiveUp, 14),
            source: null,
            CancellationToken.None);

        Assert.Equal(SharingAction.Reclaim, outcome.Action);
        Assert.True(outcome.Reclaimed);
        Assert.Equal(("client-1", "hash-1"), gateway.Calls.Single());
    }

    [Fact]
    public async Task Leaves_the_client_alone_while_the_obligation_stands()
    {
        var gateway = new RecordingGateway();
        var service = new SharingReclaimService(gateway);

        var outcome = await service.ReconcileAsync(
            Candidate(seedingMinutes: 60),
            new SharingPolicy(SharingPolicy.ModeShareThenTidy, 72, null, SharingPolicy.StuckGiveUp, 14),
            source: null,
            CancellationToken.None);

        Assert.Equal(SharingAction.Wait, outcome.Action);
        Assert.False(outcome.Reclaimed);
        Assert.Empty(gateway.Calls);
        Assert.Contains("left", outcome.Reason);
    }

    [Fact]
    public async Task Leave_alone_never_touches_the_client()
    {
        var gateway = new RecordingGateway();
        var service = new SharingReclaimService(gateway);

        var outcome = await service.ReconcileAsync(
            Candidate(seedingMinutes: 999_999),
            new SharingPolicy(SharingPolicy.ModeLeaveAlone, null, null, SharingPolicy.StuckGiveUp, 14),
            source: null,
            CancellationToken.None);

        Assert.Equal(SharingAction.Leave, outcome.Action);
        Assert.Empty(gateway.Calls);
    }

    [Fact]
    public async Task A_removal_the_client_refused_is_never_reported_as_done()
    {
        // Reporting a reclaim that did not happen is how a drive silently stays
        // full while Deluno says it tidied up — the same class of lie as an
        // import that wrote nothing and said it succeeded (#282).
        var gateway = new RecordingGateway(succeeds: false, message: "409 Conflict");
        var service = new SharingReclaimService(gateway);

        var outcome = await service.ReconcileAsync(
            Candidate(seedingMinutes: 72 * 60),
            new SharingPolicy(SharingPolicy.ModeShareThenTidy, 72, null, SharingPolicy.StuckGiveUp, 14),
            source: null,
            CancellationToken.None);

        Assert.False(outcome.Reclaimed);
        Assert.NotNull(outcome.Warning);
        Assert.Contains("409 Conflict", outcome.Warning);
    }

    [Fact]
    public async Task Usenet_is_reclaimed_at_once_because_it_never_shares()
    {
        var gateway = new RecordingGateway();
        var service = new SharingReclaimService(gateway);

        var outcome = await service.ReconcileAsync(
            Candidate(protocol: "sabnzbd", seedingMinutes: null),
            new SharingPolicy(SharingPolicy.ModeShareThenTidy, 72, null, SharingPolicy.StuckGiveUp, 14),
            source: null,
            CancellationToken.None);

        Assert.Equal(SharingAction.Reclaim, outcome.Action);
        Assert.True(outcome.Reclaimed);
    }

    [Fact]
    public async Task A_private_source_holds_on_while_the_global_rule_would_have_let_go()
    {
        // The point of the per-source override: the same completed download,
        // the same global rule, a different answer because of where it came from.
        var gateway = new RecordingGateway();
        var service = new SharingReclaimService(gateway);
        var global = new SharingPolicy(SharingPolicy.ModeShareThenTidy, 72, null, SharingPolicy.StuckGiveUp, 14);
        var candidate = Candidate(ratio: 0.3, seedingMinutes: 72 * 60);

        var withoutOverride = await service.ReconcileAsync(candidate, global, null, CancellationToken.None);
        var withOverride = await service.ReconcileAsync(candidate, global, Source(untilRatio: 2.0), CancellationToken.None);

        Assert.True(withoutOverride.Reclaimed);
        Assert.False(withOverride.Reclaimed);
        Assert.Equal(SharingAction.Wait, withOverride.Action);
    }
}
