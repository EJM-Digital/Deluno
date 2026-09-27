using Deluno.Connections.Contracts;
using Deluno.Contracts;
using Deluno.Integrations.Search;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Recovery.Policies;

namespace Deluno.Recovery.Services;

/// <summary>What was decided for one completed download, and what came of it.</summary>
/// <param name="Reclaimed">True when the client was asked to remove the item and its data.</param>
/// <param name="WouldReclaim">
/// True when the rule says this item's obligation is discharged but #814's
/// dry run held Deluno back from asking the client to remove it - the same
/// distinction the strike/blocklist executors already draw between a real
/// action and a previewed one.
/// </param>
/// <param name="Stopped">
/// True when the client was asked to stop seeding but keep the entry and its
/// files (#821) — the alternative to <paramref name="Reclaimed"/>.
/// </param>
public sealed record SharingReclaimOutcome(
    string QueueItemId,
    string Title,
    SharingAction Action,
    string Reason,
    bool Reclaimed,
    string? Warning = null,
    /// <summary>
    /// The same fact without the part a surrounding heading already states.
    /// See <see cref="SharingDecision.Detail"/>.
    /// </summary>
    string? Detail = null,
    bool WouldReclaim = false,
    bool Stopped = false);

/// <summary>
/// Turns a sharing decision into the one action Deluno is allowed to take:
/// asking the download client to let go of an item (#288).
///
/// The rule it applies is the global one, overridden by the search source the
/// release came from — a movie from a private tracker keeps sharing while the
/// same movie from a public one need not, and that distinction belongs to the
/// site rather than to the library it landed in.
///
/// Deluno never deletes a file the client still believes it owns. Doing that is
/// what breaks the share: the torrent stays registered against data that has
/// vanished, the client errors it, sharing stops, and on a private site that
/// costs the user their ratio or their account (#287). So removal is always a
/// request to the client, and the client tidies its own file.
/// </summary>
public sealed class SharingReclaimService(IDownloadClientActionGateway actions)
{
    /// <summary>
    /// Evaluates one completed, already-imported download and acts if the rule
    /// says its obligation is discharged. Anything still sharing is returned
    /// with the sentence explaining why, so the caller can show it rather than
    /// leaving the user to guess what is holding their disk.
    ///
    /// <paramref name="rules"/> and <paramref name="activityTracker"/> are
    /// optional (#820): a caller with no rule matrix at all gets exactly
    /// today's global/per-source behaviour, unchanged.
    /// </summary>
    public Task<SharingReclaimOutcome> ReconcileAsync(
        SharingReclaimCandidate candidate,
        SharingPolicy globalPolicy,
        IndexerItem? source,
        CancellationToken cancellationToken)
        => ReconcileAsync(candidate, globalPolicy, source, dryRun: false, cancellationToken);

    /// <summary>
    /// <paramref name="dryRun"/> is #814's single sandbox switch, shared with
    /// the strike/blocklist executors: the rule is still evaluated in full,
    /// but the client is never asked to remove anything while it is on.
    /// </summary>
    public async Task<SharingReclaimOutcome> ReconcileAsync(
        SharingReclaimCandidate candidate,
        SharingPolicy globalPolicy,
        IndexerItem? source,
        bool dryRun,
        CancellationToken cancellationToken,
        IReadOnlyList<SharingRuleItem>? rules = null,
        ISharingActivityTracker? activityTracker = null)
    {
        var policy = EffectivePolicyFor(globalPolicy, source, candidate.IsSeasonPack);

        SharingRuleItem? matchedRule = null;
        if (rules is { Count: > 0 })
        {
            matchedRule = SharingRuleMatcher.FindMatch(
                rules,
                new SharingRuleMatchContext(candidate.Category, TrackerHostOf(source), SplitTags(source?.Tags)));

            if (matchedRule is not null)
            {
                policy = new SharingPolicy(
                    matchedRule.Mode ?? string.Empty,
                    matchedRule.ForHours,
                    matchedRule.UntilRatio,
                    matchedRule.StuckAction ?? string.Empty,
                    matchedRule.StuckAfterDays ?? 0,
                    matchedRule.CompletionAction ?? string.Empty).InheritFrom(policy);
            }
        }

        SharingPolicyEvaluator.SharingGuards? guards = null;
        if (matchedRule is { MinSeeders: not null } or { MaxInactiveDays: not null })
        {
            var inactiveDays = matchedRule.MaxInactiveDays is > 0 && activityTracker is not null
                ? await activityTracker.RecordAndGetInactiveDaysAsync(
                    candidate.ClientId,
                    candidate.QueueItemId,
                    candidate.IsActiveNow,
                    candidate.NowUtc,
                    cancellationToken)
                : (int?)null;
            guards = new SharingPolicyEvaluator.SharingGuards(matchedRule.MinSeeders, candidate.Seeders, matchedRule.MaxInactiveDays, inactiveDays);
        }

        var decision = SharingPolicyEvaluator.Evaluate(
            policy,
            candidate.SupportsSharing,
            candidate.Ratio,
            candidate.SeedingMinutes,
            candidate.IsPrivate,
            guards);

        if (decision.Action == SharingAction.Stop)
        {
            var stopResult = await actions.StopSeedingAsync(candidate.ClientId, candidate.QueueItemId, cancellationToken);
            if (stopResult.Succeeded)
            {
                return new(
                    candidate.QueueItemId,
                    candidate.Title,
                    decision.Action,
                    decision.Reason,
                    Reclaimed: false,
                    Detail: decision.DetailOrReason,
                    Stopped: true);
            }

            var stopWarning = $"Deluno asked {candidate.ClientName} to stop seeding this and it did not: {stopResult.Message}";
            return new(
                candidate.QueueItemId,
                candidate.Title,
                decision.Action,
                decision.Reason,
                Reclaimed: false,
                Warning: stopWarning,
                Detail: $"{candidate.ClientName} would not stop seeding this — {stopResult.Message}");
        }

        if (decision.Action != SharingAction.Reclaim)
        {
            return new(
                candidate.QueueItemId,
                candidate.Title,
                decision.Action,
                decision.Reason,
                Reclaimed: false,
                Detail: decision.DetailOrReason);
        }

        if (dryRun)
        {
            return new(
                candidate.QueueItemId,
                candidate.Title,
                decision.Action,
                decision.Reason,
                Reclaimed: false,
                Detail: decision.DetailOrReason,
                WouldReclaim: true);
        }

        var result = await actions.RemoveWithDataAsync(candidate.ClientId, candidate.QueueItemId, cancellationToken);
        if (result.Succeeded)
        {
            return new(
                candidate.QueueItemId,
                candidate.Title,
                decision.Action,
                decision.Reason,
                Reclaimed: true,
                Detail: decision.DetailOrReason);
        }

        // A client that refuses to let go is the one case where the detail is
        // not a shorter form of the reason but a different fact entirely: the
        // rule was met, and the removal is what failed.
        var warning = $"Deluno asked {candidate.ClientName} to remove this and it did not: {result.Message}";
        return new(
            candidate.QueueItemId,
            candidate.Title,
            decision.Action,
            decision.Reason,
            Reclaimed: false,
            Warning: warning,
            Detail: $"{candidate.ClientName} would not let go of this — {result.Message}");
    }

    /// <summary>
    /// The global rule, with whatever the source chose to say differently laid
    /// over it. A source that has never been given its own rule contributes
    /// nothing and the global one applies unchanged.
    ///
    /// When <paramref name="isSeasonPack"/> is true and the source has its own
    /// <see cref="IndexerItem.SeasonPackSeedForHours"/>, that value takes the
    /// place of the normal seed time for this evaluation (#739): a pack is
    /// usually worth more to a private tracker, so it is asked to seed for
    /// longer than a single episode from the same source.
    /// </summary>
    public static SharingPolicy EffectivePolicyFor(SharingPolicy globalPolicy, IndexerItem? source, bool isSeasonPack = false)
    {
        if (source is null)
        {
            return globalPolicy;
        }

        var hasOverride =
            !string.IsNullOrWhiteSpace(source.SharingMode) ||
            source.SharingForHours is not null ||
            source.SharingUntilRatio is not null ||
            !string.IsNullOrWhiteSpace(source.SharingStuckAction) ||
            source.SharingStuckAfterDays is not null;

        var effective = hasOverride
            ? new SharingPolicy(
                source.SharingMode ?? string.Empty,
                source.SharingForHours,
                source.SharingUntilRatio,
                source.SharingStuckAction ?? string.Empty,
                source.SharingStuckAfterDays ?? 0,
                // No per-source completion-action override exists yet (#821 is
                // global only until #820's rule matrix arrives), so this always
                // falls through to whatever the global setting says.
                CompletionAction: string.Empty).InheritFrom(globalPolicy)
            : globalPolicy;

        if (isSeasonPack && source.SeasonPackSeedForHours is > 0)
        {
            effective = effective with { ForHours = source.SeasonPackSeedForHours };
        }

        return effective;
    }

    /// <summary>The tracker's own host, for the rule matrix's tracker-pattern matching (#820). Null when the source's URL cannot be parsed.</summary>
    private static string? TrackerHostOf(IndexerItem? source)
        => Uri.TryCreate(source?.BaseUrl, UriKind.Absolute, out var uri) ? uri.Host : null;

    private static IReadOnlyList<string>? SplitTags(string? csv)
        => string.IsNullOrWhiteSpace(csv) ? null : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>One completed download, as the client reports it.</summary>
public sealed record SharingReclaimCandidate(
    string QueueItemId,
    string ClientId,
    string ClientName,
    string Title,
    string Protocol,
    double? Ratio,
    int? SeedingMinutes,
    /// <summary>The torrent's own reported tracker privacy (#815). Null when unknown.</summary>
    bool? IsPrivate = null,
    /// <summary>
    /// The release name as the client/indexer sent it, used only to tell a
    /// season pack from a single episode (#739). Falls back to <see cref="Title"/>
    /// when a caller has nothing more specific.
    /// </summary>
    string? ReleaseName = null,
    /// <summary>The download's own category, for the rule matrix's category matching (#820).</summary>
    string? Category = null,
    /// <summary>Peers the client currently reports for this item — the closest thing Deluno has to "seeders" without an adapter change (#820).</summary>
    int? Seeders = null,
    /// <summary>Whether the client currently reports any upload or download speed on this item, for the Max Inactive Days guard (#820).</summary>
    bool IsActiveNow = true,
    DateTimeOffset? NowUtcOverride = null)
{
    /// <summary>
    /// Usenet has no sharing phase, so there is no obligation to discharge and
    /// nothing to wait for. The same judgement decides whether the import
    /// pipeline may delete a completed file itself, so it is made in one place
    /// (<see cref="DownloadProtocols.HasSharingPhase"/>) rather than twice.
    /// </summary>
    public bool SupportsSharing => DownloadProtocols.HasSharingPhase(Protocol);

    /// <summary>Whether this download is a whole-season release rather than a single episode (#739).</summary>
    public bool IsSeasonPack => SeasonPackDetector.Classify(ReleaseName ?? Title).IsSeason;

    /// <summary>Testable without a real clock; defaults to "now" for every ordinary caller.</summary>
    public DateTimeOffset NowUtc => NowUtcOverride ?? DateTimeOffset.UtcNow;
}

/// <summary>
/// The narrow slice of download-client control this service needs. Kept to one
/// method so the only thing it can do is ask a client to let go — it cannot
/// reach the filesystem, which is the whole point.
/// </summary>
public interface IDownloadClientActionGateway
{
    Task<DownloadClientRemovalResult> RemoveWithDataAsync(
        string clientId,
        string queueItemId,
        CancellationToken cancellationToken);

    /// <summary>Stops seeding without removing the entry or its files (#821).</summary>
    Task<DownloadClientRemovalResult> StopSeedingAsync(
        string clientId,
        string queueItemId,
        CancellationToken cancellationToken);
}

public sealed record DownloadClientRemovalResult(bool Succeeded, string Message);
