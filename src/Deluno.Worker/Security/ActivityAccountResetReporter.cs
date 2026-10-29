using Deluno.Jobs.Data;
using Deluno.Notifications;
using Deluno.Security.Recovery;
using Microsoft.Extensions.Logging;

namespace Deluno.Worker.Security;

/// <summary>
/// A password reset (#1392) is written to Activity in plain words and, when a notification connection is set up, sent
/// there too. Registered here because Deluno.Worker is the one place that sees Deluno.Security, Deluno.Jobs and
/// Deluno.Notifications together.
/// </summary>
public sealed class ActivityAccountResetReporter(
    IActivityFeedRepository activity,
    IOutboundNotificationService notifications,
    ILogger<ActivityAccountResetReporter> logger) : IAccountResetReporter
{
    public const string Category = "security.password-reset";

    public static string Words(RecoveryMethod method) => method == RecoveryMethod.OneTimePin
        ? "Password reset with a one-time PIN. Every signed-in session was ended."
        : "Password reset with a recovery code. Every signed-in session was ended.";

    public async Task ReportResetAsync(RecoveryMethod method, CancellationToken cancellationToken)
    {
        var words = Words(method);
        await activity.RecordActivityAsync(Category, words, null, null, null, null, cancellationToken);

        try
        {
            await notifications.DispatchAsync(Category, "Deluno password reset", words, null, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The reset has happened and is in Activity; a connection that is down must not undo that answer.
            logger.LogWarning(ex, "Could not send the password reset notification.");
        }
    }
}
