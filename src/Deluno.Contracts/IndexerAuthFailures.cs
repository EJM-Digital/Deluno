namespace Deluno.Contracts;

/// <summary>
/// Records an indexer auth failure to the combined Indexer History (#754),
/// wherever it is noticed -- an RSS sync, an interactive/automatic search, or
/// a grab. One shared sink so "search hit a bad key" and "an RSS sync hit a
/// bad key" write the same kind of row, rather than each caller keeping its
/// own copy of the same recording logic. The concrete writer (backed by
/// <c>IIndexerHistoryEventStore</c>) lives in Deluno.Api, which the callers
/// below cannot reference directly, so this interface is the shared seam.
/// </summary>
public interface IIndexerAuthFailureRecorder
{
    Task RecordAsync(string? indexerId, string indexerName, string action, string message, string? detailsJson, CancellationToken cancellationToken);
}

/// <summary>
/// The one place that decides whether a failure message looks like an expired
/// key or a bad login (#754) -- an HTTP 401/403, or a tracker exception naming
/// the credential itself. Never guessed from a status code Deluno cannot see
/// (a public tracker's own scrape failure, say): only a message that already
/// says so. Shared by every caller that only has a message string to go on
/// (an RSS feed read, a tracker's grab-time resolve); a caller that already
/// carries a typed <c>IntegrationFailureKind.Authentication</c> from the
/// resilience policy trusts that instead of re-parsing its own message.
/// </summary>
public static class IndexerAuthFailureText
{
    public static bool LooksLikeAuthFailure(string? message)
        => message is not null
           && (message.Contains("401", StringComparison.Ordinal)
               || message.Contains("403", StringComparison.Ordinal)
               || message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
               || message.Contains("api key", StringComparison.OrdinalIgnoreCase)
               || message.Contains("login", StringComparison.OrdinalIgnoreCase)
               || message.Contains("credential", StringComparison.OrdinalIgnoreCase));
}
