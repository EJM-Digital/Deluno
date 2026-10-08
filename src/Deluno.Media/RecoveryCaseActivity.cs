using System.Text.Json;
using Deluno.Contracts;
using Deluno.Jobs.Data;

namespace Deluno.Media;

/// <summary>
/// One plain Activity line for every recovery case something closed on its own (#1167). A case settled by the
/// download poller or by a deletion used to leave nothing behind, so a screen that had listed it for days simply
/// stopped listing it, and nothing said why.
/// </summary>
public static class RecoveryCaseActivity
{
    public const string Category = "recovery.case-settled";

    public static async Task RecordSettledAsync(
        IActivityFeedRepository? activityFeed,
        IEnumerable<SettledRecoveryCase>? cases,
        string reason,
        string mediaType,
        CancellationToken cancellationToken)
    {
        if (activityFeed is null || cases is null)
        {
            return;
        }

        foreach (var item in cases)
        {
            await activityFeed.RecordActivityAsync(
                Category,
                $"{item.Title}: Deluno closed the problem that was open for it. {reason}",
                JsonSerializer.Serialize(new { item.CaseId, item.FailureKind, MediaType = mediaType, Reason = reason }),
                null,
                mediaType,
                item.CaseId,
                cancellationToken);
        }
    }
}
