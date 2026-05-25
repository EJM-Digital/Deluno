namespace Deluno.Downloader.Torrent.Magnet;

/// <summary>
/// The leak-window guard for magnet ingestion.
///
/// Problem: a magnet URI does NOT carry the <c>private=1</c> flag — that
/// lives only inside the .torrent's <c>info</c> dict. Between "magnet
/// added" and "metadata downloaded", DHT/PEX MUST be used to fetch the
/// metadata. If the torrent turns out to be private, the infohash has
/// already leaked to the public DHT and the user may already be banned
/// by the time they see the first announce.
///
/// This class makes the decision BEFORE the leak happens:
/// <list type="bullet">
///   <item><description><see cref="MagnetIngestionDecision.TrackerOnly"/> — magnet has
///     trackers AND the destination looks private. Fetch metadata via the
///     trackers' peer list ONLY (DHT/PEX disabled for the metadata-fetch
///     phase). Safe.</description></item>
///   <item><description><see cref="MagnetIngestionDecision.DhtPexAllowed"/> — destination
///     is public-suspect (or explicitly public). Normal BEP-9.</description></item>
///   <item><description><see cref="MagnetIngestionDecision.RefuseUnlessOverridden"/> —
///     destination looks private AND the magnet has no trackers. The only
///     fetch path is DHT/PEX, which leaks. Throw <see cref="PrivateMagnetLeakException"/>
///     unless the caller passes <see cref="MagnetIngestionHint.UserAcceptedLeakRisk"/>.</description></item>
/// </list>
///
/// Decide() is pure logic — testable without MonoTorrent or live swarms.
/// ResolveMetadataAsync() carries the actual MonoTorrent integration and
/// is the real-network seam.
/// </summary>
public sealed class MagnetIngestor
{
    public static MagnetIngestionDecision Decide(MagnetLinkData magnet, MagnetIngestionHint hint)
    {
        ArgumentNullException.ThrowIfNull(magnet);
        ArgumentNullException.ThrowIfNull(hint);

        // Treat as private-suspect if either the destination category
        // says so explicitly OR the magnet's trackers look like private-
        // tracker passkey URLs.
        var privateSuspect = hint.IsPrivateSuspect ?? magnet.LooksPrivate;

        if (privateSuspect)
        {
            return magnet.HasTrackers
                ? MagnetIngestionDecision.TrackerOnly
                : hint.UserAcceptedLeakRisk
                    ? MagnetIngestionDecision.DhtPexAllowed
                    : MagnetIngestionDecision.RefuseUnlessOverridden;
        }

        return MagnetIngestionDecision.DhtPexAllowed;
    }

    /// <summary>
    /// Throws if the magnet would require DHT/PEX leak and the user
    /// hasn't opted in. Designed to be called before any MonoTorrent
    /// metadata-fetch call site.
    /// </summary>
    public static void GuardOrThrow(MagnetLinkData magnet, MagnetIngestionHint hint)
    {
        if (Decide(magnet, hint) == MagnetIngestionDecision.RefuseUnlessOverridden)
        {
            throw new PrivateMagnetLeakException(magnet.InfohashV1Hex ?? magnet.InfohashV2Hex ?? "?");
        }
    }
}

public enum MagnetIngestionDecision
{
    /// <summary>Safe path: fetch metadata via trackers only, DHT/PEX off.</summary>
    TrackerOnly,
    /// <summary>Normal path: BEP-9 with DHT/PEX enabled. Public-suspect destinations.</summary>
    DhtPexAllowed,
    /// <summary>
    /// Private-suspect + no trackers in the magnet. Default action is
    /// refuse; caller can override by setting
    /// <see cref="MagnetIngestionHint.UserAcceptedLeakRisk"/>.
    /// </summary>
    RefuseUnlessOverridden,
}

/// <summary>
/// Per-ingestion hints. Comes from the orchestrator's knowledge of the
/// job's destination category / library — not from the magnet itself.
/// </summary>
/// <param name="IsPrivateSuspect">
/// Explicit hint from the orchestrator. <c>null</c> means "no opinion —
/// fall back to <see cref="MagnetLinkData.LooksPrivate"/> heuristic".
/// </param>
/// <param name="UserAcceptedLeakRisk">
/// True only when the user has clicked through the
/// "metadata fetch will leak the infohash to public DHT — continue?"
/// modal. Must NOT be defaulted to true by any non-UI code path.
/// </param>
public sealed record MagnetIngestionHint(
    bool? IsPrivateSuspect = null,
    bool UserAcceptedLeakRisk = false);

public sealed class PrivateMagnetLeakException : Exception
{
    public string Infohash { get; }
    public PrivateMagnetLeakException(string infohash)
        : base(
            $"Magnet {infohash} is destined for a private-suspect category but has no trackers " +
            "listed. Fetching metadata would require DHT/PEX, which would leak the infohash to " +
            "public networks and likely get the user's private-tracker account banned. Either: " +
            "(a) attach the .torrent file instead, or (b) re-add with UserAcceptedLeakRisk=true " +
            "after surfacing the risk to the user.")
        => Infohash = infohash;
}
