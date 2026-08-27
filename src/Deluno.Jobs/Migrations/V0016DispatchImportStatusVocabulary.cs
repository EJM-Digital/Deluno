using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Jobs.Migrations;

/// <summary>
/// One word for a finished import, because two of them cost us the archive.
///
/// Every writer of <c>import_status</c> has always written <c>imported</c> —
/// the repository's own SQL says so, gating <c>import_completed_utc</c> on
/// <c>@importStatus IN ('imported', 'failed')</c>. Three readers asked for
/// <c>completed</c> instead, so they matched nothing:
///
/// - the query that selects dispatches to archive, which meant nothing was ever
///   archived and every imported dispatch stayed in the active set for good;
/// - <c>successful_imports</c> in the dispatch metrics, which served zero;
/// - the poller's realtime success backstop.
///
/// Two other call sites had already noticed and papered over it locally by
/// accepting either word. This normalises any row that somehow holds the other
/// word so the readers can be pointed at one, and the workarounds dropped.
/// </summary>
public sealed class V0016DispatchImportStatusVocabulary : SqliteSqlMigration
{
    public override int Version => 16;

    public override string Name => "dispatch_import_status_vocabulary";

    protected override string Sql =>
        """
        UPDATE download_dispatches
        SET import_status = 'imported'
        WHERE import_status = 'completed';
        """;
}
