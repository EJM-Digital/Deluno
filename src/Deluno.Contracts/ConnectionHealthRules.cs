namespace Deluno.Contracts;

/// <summary>
/// The one rule for whether a stored connection health means the connection is down or not working (amber), read by the
/// tray and by the Dashboard's connections (#1213). Healthy, untested and unknown are fine; anything else is not:
/// degraded (such as a refused credential), unreachable, unhealthy, failed, error.
/// </summary>
public static class ConnectionHealthRules
{
    public static bool IsDown(string? status)
        => !string.IsNullOrWhiteSpace(status)
           && status.Trim().ToLowerInvariant() is not ("healthy" or "untested" or "unknown");
}
