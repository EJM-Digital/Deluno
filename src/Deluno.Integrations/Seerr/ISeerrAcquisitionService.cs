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
    Task<SeerrAcquisitionOutcome> AcquireAsync(
        SeerrConnection connection,
        string mediaType,
        string? tmdbId,
        string? tvdbId,
        int seerrMediaId,
        CancellationToken cancellationToken);
}

/// <summary>What came of turning one approval into an acquisition.</summary>
public sealed record SeerrAcquisitionOutcome(bool Ok, string Message);
