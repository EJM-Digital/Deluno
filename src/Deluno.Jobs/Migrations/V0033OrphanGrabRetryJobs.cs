using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Jobs.Migrations;

/// <summary>
/// Clears the <c>download.grab.retry</c> jobs nothing would ever run (#620).
///
/// <para>A failed grab that carried a dispatch id queued one of these. No
/// handler was registered for the type and no worker lane leased it, so each
/// one sat queued for good, where Activity counted it as work still to do. Its
/// payload also held the release's download link, which can carry an indexer
/// key. Deluno no longer queues them, and it no longer says a failed grab will
/// be retried.</para>
///
/// <para>A grab that already failed keeps the failure it recorded, which says
/// "Retry scheduled" in Queue. It is corrected to what happened: not sent, and
/// not sent again by Deluno. A rate limit keeps the time the client asked for,
/// because that is still true.</para>
/// </summary>
public sealed class V0033OrphanGrabRetryJobs : SqliteSqlMigration
{
    public override int Version => 33;

    public override string Name => "orphan_grab_retry_jobs";

    protected override string Sql =>
        """
        DELETE FROM job_queue
        WHERE job_type = 'download.grab.retry';

        UPDATE download_dispatches
        SET grab_failure_json = CASE
            WHEN json_extract(grab_failure_json, '$.Kind') = 'RateLimit'
                THEN json_set(grab_failure_json, '$.RetryState', 'NotRetryable')
            ELSE json_set(grab_failure_json, '$.RetryState', 'NotRetryable', '$.RetryAfterUtc', NULL)
        END
        WHERE grab_failure_json IS NOT NULL
          AND json_valid(grab_failure_json)
          AND json_extract(grab_failure_json, '$.RetryState') IN ('RetryScheduled', 'Retrying');
        """;
}
