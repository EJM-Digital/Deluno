using Deluno.Platform.Contracts;

namespace Deluno.Platform.Data;

/// <summary>
/// Durable memory of when the internet was down, so a stall or strike window
/// started before an outage and still open after it can subtract exactly the
/// outage time it overlaps rather than losing that history on a restart
/// (#901).
/// </summary>
public interface IConnectivityOutageRepository
{
    /// <summary>Every outage window still worth keeping, oldest first.</summary>
    Task<IReadOnlyList<ConnectivityOutageWindow>> ListWindowsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Opens a new window. A caller that calls this while one is already open
    /// (no <see cref="ConnectivityOutageWindow.EndUtc"/>) is a bug elsewhere:
    /// this does not itself close the previous one.
    /// </summary>
    Task RecordOutageStartedAsync(DateTimeOffset startUtc, CancellationToken cancellationToken);

    /// <summary>Closes the most recently opened window that has no end yet, if any.</summary>
    Task RecordOutageEndedAsync(DateTimeOffset endUtc, CancellationToken cancellationToken);
}
