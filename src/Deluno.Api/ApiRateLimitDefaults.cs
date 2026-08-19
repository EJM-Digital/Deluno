namespace Deluno.Api;

/// <summary>
/// Defaults for the global <c>/api</c> rate limiter.
///
/// This limiter exists to protect Deluno from a misbehaving external
/// caller — a third-party script or integration driving it through a
/// generated <c>deluno_</c>-prefixed API key. It does not apply to Deluno's
/// own UI or its background jobs: <see cref="ApiRateLimitPartitionKeyResolver"/>
/// exempts any request presenting the browser's own session token instead of
/// a real API key, so internal traffic is never throttled regardless of how
/// many tabs of the same login are open or how often the dashboard polls.
/// <see cref="DefaultPermitLimit"/> only has to be generous enough for
/// legitimate external automation, not sized against Deluno's own request
/// volume.
/// </summary>
public static class ApiRateLimitDefaults
{
    public const int DefaultPermitLimit = 3000;
    public const int DefaultWindowSeconds = 60;
}
