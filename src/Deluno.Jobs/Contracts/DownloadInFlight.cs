namespace Deluno.Jobs.Contracts;

/// <summary>
/// A download on its way: sent to a client and not yet imported, refused at
/// import, or archived, whether or not the client has shown it yet (#554, #555).
/// </summary>
public sealed record DownloadInFlight(
    string EntityType,
    string EntityId,
    string ReleaseName);
