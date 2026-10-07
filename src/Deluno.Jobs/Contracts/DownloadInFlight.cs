namespace Deluno.Jobs.Contracts;

/// <summary>
/// A download on its way: sent to a client and not yet imported, refused at
/// import, or archived, whether or not the client has shown it yet (#554, #555).
/// </summary>
/// <param name="NeverShown">
/// Ended as never shown by its client. It still keeps its title off the work
/// list, because the client may only be unreachable, but it is no reason not to
/// send the release again: nothing says it is coming.
/// </param>
/// <param name="ClientId">The client it was sent to, so a hand send can name it and a Replace can remove it (#1175).</param>
/// <param name="ClientName">That client's name as it was when the release was sent.</param>
/// <param name="QueueItemId">The id the client knows it by (hash or item id), when the client gave one.</param>
public sealed record DownloadInFlight(
    string EntityType,
    string EntityId,
    string ReleaseName,
    bool NeverShown = false,
    string? ClientId = null,
    string? ClientName = null,
    string? QueueItemId = null);
