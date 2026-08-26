using Deluno.Connections.Contracts;
using Deluno.Platform.Contracts;
using Deluno.Recovery.Policies;

namespace Deluno.Recovery.Services;

/// <summary>What was decided for one completed download, and what came of it.</summary>
/// <param name="Reclaimed">True when the client was asked to remove the item and its data.</param>
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
    string? Detail = null);

/// <summary>
/// Turns a sharing decision into the one action Deluno is allowed to take:
/// asking the download client to let go of an item (#288).
///
/// The rule it applies is the global one, overridden by the search source the
/// release came from — a film from a private tracker keeps sharing while the
/// same film from a public one need not, and that distinction belongs to the
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
    /// </summary>
    public async Task<SharingReclaimOutcome> ReconcileAsync(
        SharingReclaimCandidate candidate,
        SharingPolicy globalPolicy,
        IndexerItem? source,
        CancellationToken cancellationToken)
    {
        var policy = EffectivePolicyFor(globalPolicy, source);
        var decision = SharingPolicyEvaluator.Evaluate(
            policy,
            candidate.SupportsSharing,
            candidate.Ratio,
            candidate.SeedingMinutes);

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
    /// </summary>
    public static SharingPolicy EffectivePolicyFor(SharingPolicy globalPolicy, IndexerItem? source)
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

        if (!hasOverride)
        {
            return globalPolicy;
        }

        return new SharingPolicy(
            source.SharingMode ?? string.Empty,
            source.SharingForHours,
            source.SharingUntilRatio,
            source.SharingStuckAction ?? string.Empty,
            source.SharingStuckAfterDays ?? 0).InheritFrom(globalPolicy);
    }
}

/// <summary>One completed download, as the client reports it.</summary>
public sealed record SharingReclaimCandidate(
    string QueueItemId,
    string ClientId,
    string ClientName,
    string Title,
    string Protocol,
    double? Ratio,
    int? SeedingMinutes)
{
    /// <summary>
    /// Usenet has no sharing phase, so there is no obligation to discharge and
    /// nothing to wait for.
    /// </summary>
    public bool SupportsSharing => !string.Equals(Protocol, "sabnzbd", StringComparison.OrdinalIgnoreCase)
                                   && !string.Equals(Protocol, "nzbget", StringComparison.OrdinalIgnoreCase);
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
}

public sealed record DownloadClientRemovalResult(bool Succeeded, string Message);
