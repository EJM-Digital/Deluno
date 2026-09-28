using Deluno.Connections.Contracts;

namespace Deluno.Integrations.Seerr;

/// <summary>
/// Turns an approved Seerr request into a real Deluno acquisition (#851):
/// adds the title to the appropriate library (honouring the connection's own
/// library/quality-profile mapping) and triggers a search, the same as
/// adding it manually. The implementation lives in Deluno.Worker, which is
/// the layer that can already reach both the movie and series engines — this
/// interface exists so the inbound webhook route (here, in
/// Deluno.Integrations) never has to.
/// </summary>
public interface ISeerrAcquisitionService
{
    /// <param name="is4k">Whether Seerr marked this a 4K request (#985), so it can be routed to the connection's 4K library instead of the normal one.</param>
    /// <param name="requestedBy">Who asked for it in Seerr (#985), the way Seerr showed them; null when the webhook sent none.</param>
    Task<SeerrAcquisitionOutcome> AcquireAsync(
        SeerrConnection connection,
        string mediaType,
        string? tmdbId,
        string? tvdbId,
        int seerrMediaId,
        bool is4k,
        string? requestedBy,
        CancellationToken cancellationToken);
}

/// <summary>What came of turning one approval into an acquisition.</summary>
public sealed record SeerrAcquisitionOutcome(bool Ok, string Message);
