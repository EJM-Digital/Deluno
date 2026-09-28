namespace Deluno.Security.Hardening;

/// <summary>
/// Records "skip sign-in for local devices" (#993) turning on or off to
/// Activity, in plain words -- the same rule every automatic behaviour
/// follows. Deluno.Security cannot reference Deluno.Jobs (Activity's home)
/// directly without a circular project reference (Deluno.Jobs already
/// depends on Deluno.Quality, which depends back on Deluno.Security), so the
/// host composition root supplies the real implementation; a no-op default
/// keeps this feature usable for any composition that does not register one.
/// </summary>
public interface ILocalNetworkAuthChangeNotifier
{
    Task NotifyChangedAsync(bool enabled, CancellationToken cancellationToken);
}

public sealed class NoOpLocalNetworkAuthChangeNotifier : ILocalNetworkAuthChangeNotifier
{
    public Task NotifyChangedAsync(bool enabled, CancellationToken cancellationToken) => Task.CompletedTask;
}
