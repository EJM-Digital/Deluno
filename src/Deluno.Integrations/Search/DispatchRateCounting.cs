namespace Deluno.Integrations.Search;

/// <summary>
/// Which sends count toward a download client's or an indexer's success rate (#1222), in one place so the client
/// rate and the indexer rate cannot drift apart. Only a send with a final outcome that is the source's doing counts:
/// it was imported, or the client reported the download failed (or the finished file was not what it claimed, or the
/// client refused the send). A send still in flight, one a person removed or archived, one a strike rule gave up on
/// (<c>health-remediation-applied</c>), one contained as not needed (<c>handed-over-elsewhere</c>), one Deluno's own
/// import lost, and one the client no longer holds (<c>not_found</c>) are not the source's doing and never count.
/// </summary>
public static class DispatchRateCounting
{
    /// <summary>The most recent counted sends the rate is worked out over.</summary>
    public const int RecentSendLimit = 20;

    /// <summary>Counted sends older than this many days have aged out of the rate.</summary>
    public const int RecentWindowDays = 30;

    /// <summary>Fewer counted sends than this is no record at all, never a verdict.</summary>
    public const int MinimumCountedSends = 3;

    /// <summary>A passed-over client that has had no send for this long is given a trial send again.</summary>
    public static readonly TimeSpan ClientCoolOff = TimeSpan.FromHours(12);

    /// <summary>The send was imported: a success.</summary>
    public const string ImportedSql = "import_status = 'imported'";

    /// <summary>The send failed in the client: a failure that is the source's doing.</summary>
    public const string FailedInClientSql =
        "(COALESCE(import_status, '') != 'imported' AND ("
        + "(import_status = 'failed' AND COALESCE(import_failure_code, '') IN ('client-reported-failure', 'download-integrity-failed'))"
        + " OR (grab_status = 'failed' AND COALESCE(grab_failure_code, '') != 'not_found')))";

    /// <summary>The rows that count toward the rate, whichever way they went.</summary>
    public const string CountsTowardRateSql = "(" + ImportedSql + " OR " + FailedInClientSql + ")";

    /// <summary>When the send reached its outcome; the clock the window and the recency order use.</summary>
    public const string OutcomeTimeSql = "COALESCE(import_completed_utc, grab_attempted_utc, created_utc)";
}
