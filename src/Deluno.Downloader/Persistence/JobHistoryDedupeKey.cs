using System.Security.Cryptography;
using System.Text;
using Deluno.Downloader.Engine;

namespace Deluno.Downloader.Persistence;

/// <summary>
/// Computes the canonical <c>history.dedupe_key</c> for a completed job.
///
/// <para><b>Purpose:</b> when a request comes in to grab a release (from
/// Sonarr, Radarr, or our own request pipeline), we want to be able to
/// answer "did we already complete this?" — even after the live job row
/// has been archived to history. <c>dedupe_key</c> is the stable
/// fingerprint that links a request to a completed history row without
/// needing the original job id (which the requester doesn't know).</para>
///
/// <para><b>Per-protocol formula:</b></para>
/// <list type="bullet">
///   <item>
///     <b>Torrent</b> — <c>torrent:&lt;infohashV1-hex&gt;</c> when
///     available; <c>torrent:btmh:&lt;multihash-hex&gt;</c> for v2-only
///     torrents. Infohash is the only objective content identifier in
///     the BitTorrent protocol, so this is precisely as collision-safe
///     as torrents themselves are.
///   </item>
///   <item>
///     <b>NZB</b> — <c>nzb:&lt;sha256(display_name + ":" + total_bytes)&gt;</c>.
///     NZBs don't have a protocol-level content fingerprint (the
///     yEnc-decoded payload would be perfect but only the orchestrator
///     sees it). display-name + size is what release groups + Usenet
///     indexers use as their de facto identity, and matches the user's
///     mental model: same name + same size = same release.
///     Different-quality re-issues (1080p vs 2160p of the same title)
///     get different sizes, so they don't collide.
///   </item>
/// </list>
///
/// <para><b>Why size and not first-segment message-id for NZB:</b> the
/// orchestrator discards segment-level state when it archives a job,
/// and message-ids are sometimes mangled by indexers anyway. Total
/// bytes is something we definitely retain (it's a top-level column on
/// <c>jobs</c> + <c>history</c>) and is observably stable across the
/// same release re-posted by the same group.</para>
///
/// <para><b>NULL key:</b> caller may pass <c>null</c> if the job
/// didn't reach a state where the key was computable (e.g. failed
/// before the torrent metadata resolved). The schema permits NULL and
/// the request-pipeline treats "no key" as "no match" — safe default.</para>
/// </summary>
public static class JobHistoryDedupeKey
{
    /// <summary>
    /// Computes the dedupe_key for <paramref name="job"/>. Returns null
    /// if the job doesn't have enough resolved data yet (e.g. a torrent
    /// that failed before metadata download — no infohash known).
    /// </summary>
    /// <param name="job">The job at archive time. <c>TotalBytes</c> must
    /// be the resolved post-metadata size for torrents.</param>
    /// <param name="torrentInfohashV1Hex">For torrents only — the 40-char
    /// hex infohash. Pass null for V2-only torrents or NZB jobs.</param>
    /// <param name="torrentInfohashV2Hex">For V2-only torrents — the
    /// multihash hex (longer than V1). Pass null otherwise.</param>
    public static string? Compute(
        JobRecord job,
        string? torrentInfohashV1Hex = null,
        string? torrentInfohashV2Hex = null)
    {
        return job.Protocol switch
        {
            DownloadProtocol.Torrent => ComputeTorrent(torrentInfohashV1Hex, torrentInfohashV2Hex),
            DownloadProtocol.Nzb => ComputeNzb(job.DisplayName, job.TotalBytes),
            _ => null,
        };
    }

    /// <summary>
    /// NZB-specific overload for callers that already know the
    /// canonical (display_name, total_bytes) pair.
    /// </summary>
    public static string ComputeNzb(string displayName, long totalBytes)
    {
        // SHA-256 of "display_name:total_bytes". Lowercase hex output;
        // display name is left case-sensitive because scene release
        // groups can legitimately distinguish releases by case.
        var input = $"{displayName}:{totalBytes}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return "nzb:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Torrent-specific overload. V1 hex preferred; V2 used only when
    /// V1 is unavailable (rare; only v2-only torrents).
    /// </summary>
    public static string? ComputeTorrent(string? infohashV1Hex, string? infohashV2Hex)
    {
        if (!string.IsNullOrEmpty(infohashV1Hex))
            return "torrent:" + infohashV1Hex.ToLowerInvariant();
        if (!string.IsNullOrEmpty(infohashV2Hex))
            return "torrent:btmh:" + infohashV2Hex.ToLowerInvariant();
        return null;
    }
}
