using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Jobs.Migrations;

/// <summary>
/// #1098: a problem the bell lists that has no row of its own to settle (a title with no acceptable release)
/// can be dismissed, and stays dismissed until the title stops being that problem. One row per dismissed item.
/// </summary>
public sealed class V0049AttentionDismissals : SqliteSqlMigration
{
    public override int Version => 49;

    public override string Name => "attention_dismissals";

    protected override string Sql =>
        """
        CREATE TABLE IF NOT EXISTS attention_dismissals (
            dismissal_key TEXT NOT NULL PRIMARY KEY,
            dismissed_utc TEXT NOT NULL
        );
        """;
}
