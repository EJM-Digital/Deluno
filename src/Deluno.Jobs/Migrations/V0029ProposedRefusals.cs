using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Jobs.Migrations;

/// <summary>
/// Whether a blocklist entry is a decision or a question.
///
/// <para>The rules screen offers <i>ask me</i> alongside the three automatic
/// answers, and "ask me" has to leave something behind or it is just a slower
/// way of doing nothing. A proposed refusal is that something: it is recorded
/// with its reason, it is <b>not</b> applied to searches, and it waits on the
/// blocklist until somebody says refuse it or allow it.</para>
///
/// <para>Existing rows are refusals, because until now that is the only kind
/// there was.</para>
/// </summary>
public sealed class V0029ProposedRefusals : SqliteSqlMigration
{
    public override int Version => 29;

    public override string Name => "proposed_refusals";

    protected override string Sql =>
        "ALTER TABLE blocked_releases ADD COLUMN state TEXT NOT NULL DEFAULT 'refused';";
}
