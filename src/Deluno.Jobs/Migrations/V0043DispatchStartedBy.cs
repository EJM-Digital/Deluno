using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Jobs.Migrations;

/// <summary>
/// #1098: why a download was started (a person, Seerr, a list, a missing-title search or an
/// upgrade search), written when the download is sent so the Live journey can tag its card.
/// Older rows stay null and show no tag. The detail is the list's provider name.
/// </summary>
public sealed class V0043DispatchStartedBy : SqliteSqlMigration
{
    public override int Version => 43;

    public override string Name => "dispatch_started_by";

    protected override string Sql =>
        """
        ALTER TABLE download_dispatches ADD COLUMN started_by TEXT NULL;
        ALTER TABLE download_dispatches ADD COLUMN started_by_detail TEXT NULL;
        """;
}
