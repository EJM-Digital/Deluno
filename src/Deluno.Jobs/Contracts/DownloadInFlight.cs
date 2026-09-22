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
public sealed record DownloadInFlight(
    string EntityType,
    string EntityId,
    string ReleaseName,
    bool NeverShown = false);
