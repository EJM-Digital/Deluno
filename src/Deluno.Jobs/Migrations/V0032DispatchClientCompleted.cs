using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Jobs.Migrations;

/// <summary>
/// When the download client said it had finished with a download, kept apart
/// from when it first showed it (#554).
///
/// <para><c>detected_utc</c> was only ever written by the client's completion
/// webhook, so "detected" meant "finished", and the poller's four-hour wait for
/// an import was measured from it. Without a webhook nothing wrote it at all:
/// on the rig, eight downloads SABnzbd had completed, and Deluno had handed to
/// Weir, were failed two hours after their grabs as never shown by the client.
/// Polling now records both facts. A download can be on screen in the client for
/// many hours before it finishes, and only the second one starts the wait for
/// an import.</para>
///
/// <para>Every existing <c>detected_utc</c> came from that completion webhook,
/// so it is carried across as the completion time.</para>
/// </summary>
public sealed class V0032DispatchClientCompleted : SqliteSqlMigration
{
    public override int Version => 32;

    public override string Name => "dispatch_client_completed";

    protected override string Sql =>
        """
        ALTER TABLE download_dispatches ADD COLUMN client_completed_utc TEXT NULL;

        UPDATE download_dispatches
        SET client_completed_utc = detected_utc
        WHERE detected_utc IS NOT NULL;
        """;
}
