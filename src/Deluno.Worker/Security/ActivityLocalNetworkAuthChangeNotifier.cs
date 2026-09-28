using Deluno.Jobs.Data;
using Deluno.Security.Hardening;

namespace Deluno.Worker.Security;

/// <summary>
/// Writes "skip sign-in for local devices" (#993) turning on or off to
/// Activity, in plain words -- registered here because Deluno.Worker is the
/// one place that can see both Deluno.Security and Deluno.Jobs without a
/// circular project reference.
/// </summary>
public sealed class ActivityLocalNetworkAuthChangeNotifier(IActivityFeedRepository activity) : ILocalNetworkAuthChangeNotifier
{
    public Task NotifyChangedAsync(bool enabled, CancellationToken cancellationToken)
        => activity.RecordActivityAsync(
            "security.local-network-auth",
            enabled
                ? "Skip sign-in for devices on the local network turned on."
                : "Skip sign-in for devices on the local network turned off.",
            null,
            null,
            null,
            null,
            cancellationToken);
}
