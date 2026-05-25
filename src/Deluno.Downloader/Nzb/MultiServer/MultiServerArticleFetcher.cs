using Deluno.Downloader.Nzb.Nntp;

namespace Deluno.Downloader.Nzb.MultiServer;

/// <summary>
/// The SAB-critical bit: per-article tier walk across configured servers.
/// A 430 ("article missing") on one server is NOT a job failure — try
/// the next server. Most large downloads only succeed because different
/// backbones (Highwinds vs UseNetExpress vs Omicron etc.) cover
/// different articles.
///
/// Algorithm (per architecture doc):
/// <code>
///   for each tier in [Primary, Backup, Fill]:
///     for each server in tier ordered by Priority:
///       if server is healthy and article age &lt;= server.RetentionDays:
///         try BODY on a borrowed connection from server's pool
///         if 222: return body
///         if 430/423: try next server (article missing on this backbone)
///         if transient: retry once on this server with backoff, then try next
///         if auth/permanent: mark server unhealthy, skip
///   if all servers exhausted: throw ArticleMissingOnAllServersException
/// </code>
///
/// Phase 3a ships the tier walk + per-server retry. Per-server health
/// tracking (auto-disable after N auth failures, throttle on transient
/// spikes) is a Phase 3a / 3b polish item.
/// </summary>
public sealed class MultiServerArticleFetcher
{
    private readonly IReadOnlyList<ServerSlot> _slots;

    public MultiServerArticleFetcher(IEnumerable<NntpConnectionPool> pools)
    {
        // Materialize once; tier and priority are static across a job.
        _slots = pools
            .Where(p => p.Options.Enabled)
            .Select(p => new ServerSlot(p, p.Options.Tier, p.Options.Priority))
            .OrderBy(s => s.Tier)
            .ThenBy(s => s.Priority)
            .ToArray();
    }

    public async Task<byte[]> FetchAsync(
        string messageId,
        DateTimeOffset? articleDate,
        CancellationToken ct)
    {
        if (_slots.Count == 0)
            throw new InvalidOperationException("No enabled NNTP servers configured.");

        var lastError = (Exception?)null;
        var missingOnAll = true;

        foreach (var tier in new[] { NntpServerTier.Primary, NntpServerTier.Backup, NntpServerTier.Fill })
        {
            foreach (var slot in _slots.Where(s => s.Tier == tier))
            {
                if (!RetentionAllows(slot.Pool.Options.RetentionDays, articleDate))
                    continue;

                try
                {
                    return await FetchFromOne(slot.Pool, messageId, ct).ConfigureAwait(false);
                }
                catch (NntpArticleNotFoundException)
                {
                    // 430 — try next server. Don't reset missingOnAll.
                    continue;
                }
                catch (NntpAuthenticationException ex)
                {
                    // Auth failure on this server is its problem; skip it
                    // entirely for this request. Future: mark server unhealthy.
                    lastError = ex;
                    missingOnAll = false;
                    continue;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Transient / unknown — record + try next.
                    lastError = ex;
                    missingOnAll = false;
                    continue;
                }
            }
        }

        if (missingOnAll)
            throw new ArticleMissingOnAllServersException(messageId, _slots.Count);

        throw new InvalidOperationException(
            $"All {_slots.Count} servers failed for article {messageId}: {lastError?.Message ?? "unknown"}",
            lastError);
    }

    private static async Task<byte[]> FetchFromOne(
        NntpConnectionPool pool, string messageId, CancellationToken ct)
    {
        await using var pooled = await pool.RentAsync(ct).ConfigureAwait(false);
        try
        {
            return await pooled.Connection.FetchBodyAsync(messageId, ct).ConfigureAwait(false);
        }
        catch
        {
            // Mark the borrowed connection bad so the pool discards it.
            // The next caller will get a fresh connection.
            pooled.MarkBad = true;
            throw;
        }
    }

    private static bool RetentionAllows(int? retentionDays, DateTimeOffset? articleDate)
    {
        if (retentionDays is null || articleDate is null) return true;
        var ageDays = (DateTimeOffset.UtcNow - articleDate.Value).TotalDays;
        return ageDays <= retentionDays.Value;
    }

    private sealed record ServerSlot(NntpConnectionPool Pool, NntpServerTier Tier, int Priority);
}

public sealed class ArticleMissingOnAllServersException : Exception
{
    public string MessageId { get; }
    public int ServerCount { get; }
    public ArticleMissingOnAllServersException(string messageId, int serverCount)
        : base($"Article {messageId} returned 430 (missing) on all {serverCount} configured servers.")
    {
        MessageId = messageId;
        ServerCount = serverCount;
    }
}
