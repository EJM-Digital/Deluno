namespace Deluno.Platform.Contracts;

/// <summary>
/// One continuous stretch the internet was down, kept so a stall or strike
/// window can subtract exactly the outage time it overlaps (#901).
/// <see cref="EndUtc"/> is null while the outage is still open.
/// </summary>
public sealed record ConnectivityOutageWindow(DateTimeOffset StartUtc, DateTimeOffset? EndUtc);
