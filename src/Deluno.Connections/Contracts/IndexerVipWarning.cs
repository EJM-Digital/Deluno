namespace Deluno.Connections.Contracts;

/// <summary>
/// Whether a private-tracker indexer's recorded VIP/subscription expiry
/// (#747) should currently show a warning: starting 7 days before the
/// recorded date and continuing after it lapses, until the user
/// acknowledges that exact date or the date itself changes.
/// </summary>
public static class IndexerVipWarning
{
    public const int WarningWindowDays = 7;

    public static bool IsActive(IndexerItem item, DateTimeOffset now)
        => IsActive(item.VipExpiresUtc, item.VipWarningAcknowledgedExpiryUtc, now);

    public static bool IsActive(DateTimeOffset? vipExpiresUtc, DateTimeOffset? acknowledgedForExpiryUtc, DateTimeOffset now)
    {
        if (vipExpiresUtc is not { } expiry)
        {
            return false;
        }

        // Acknowledging (or the date itself being edited to a different
        // value) silences the warning for that specific expiry, and only
        // that one — a later renewal that lapses again warns afresh.
        if (acknowledgedForExpiryUtc == expiry)
        {
            return false;
        }

        return now >= expiry.AddDays(-WarningWindowDays);
    }

    /// <summary>True once the recorded date itself has passed, for the "expired" vs "expiring soon" wording.</summary>
    public static bool HasLapsed(DateTimeOffset vipExpiresUtc, DateTimeOffset now)
        => now >= vipExpiresUtc;
}
