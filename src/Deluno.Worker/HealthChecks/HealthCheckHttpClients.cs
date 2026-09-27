namespace Deluno.Worker.HealthChecks;

/// <summary>Named <see cref="System.Net.Http.HttpClient"/> instances used only by health checks (#868).</summary>
public static class HealthCheckHttpClients
{
    /// <summary>
    /// Used to read a trusted time source's response headers for
    /// <see cref="Checks.SystemTimeHealthCheck"/>. Short timeout: a slow
    /// answer here should read as "could not confirm the clock", not hold up
    /// the whole health pass.
    /// </summary>
    public const string SystemTime = "health-check-system-time";
}
