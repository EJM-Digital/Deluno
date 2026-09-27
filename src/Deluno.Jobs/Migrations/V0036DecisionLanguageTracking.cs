using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Jobs.Migrations;

public sealed class V0036DecisionLanguageTracking : SqliteSqlMigration
{
    public override int Version => 36;

    public override string Name => "decision_language_tracking";

    protected override string Sql =>
        """
        ALTER TABLE download_dispatches ADD COLUMN decision_language TEXT NULL;
        """;
}
