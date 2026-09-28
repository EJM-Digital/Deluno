using Deluno.Notifications.Contracts;
using Microsoft.Extensions.Logging;

namespace Deluno.Notifications;

/// <summary>
/// Every caller that fires an outbound notification for one of its own
/// lifecycle events (grab, upgrade, rename, delete, ...) needs the same three
/// things: read the toggle for that event, skip quietly when it is off, and
/// never let a misconfigured webhook fail the action that triggered it.
///
/// <para>That shape used to live only inside <c>ImportPipelineService</c>
/// (#856). Every other caller — a file deleted from a title's Files tab, a
/// title removed with its files, retention deleting a title's or an
/// episode's files — needs the identical guard, so it is one helper rather
/// than a fourth private copy.</para>
/// </summary>
public static class NotificationOutboundDispatch
{
    public static async Task DispatchIfEnabledAsync(
        IOutboundNotificationService? outboundNotificationService,
        INotificationService? notificationService,
        string eventCategory,
        string title,
        string message,
        string? detailsJson,
        Func<NotificationPreferences, bool> isEnabled,
        string outcomeLabel,
        ILogger logger,
        CancellationToken cancellationToken,
        // The tags of the title this event is about, when there is one (#990). Null when the event names no title.
        IReadOnlyList<string>? titleTags = null)
    {
        if (outboundNotificationService is null)
        {
            return;
        }

        try
        {
            if (notificationService is not null)
            {
                var preferences = await notificationService.GetPreferencesAsync(cancellationToken);
                if (!isEnabled(preferences))
                {
                    return;
                }
            }

            await outboundNotificationService.DispatchAsync(eventCategory, title, message, detailsJson, cancellationToken, titleTags);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Outbound notification dispatch failed for {Outcome} — notifications may be misconfigured.", outcomeLabel);
        }
    }
}
