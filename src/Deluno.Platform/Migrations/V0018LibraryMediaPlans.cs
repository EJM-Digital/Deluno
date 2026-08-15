using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

public sealed class V0018LibraryMediaPlans : SqliteSqlMigration
{
    public override int Version => 18;

    public override string Name => "library_media_plans";

    protected override string Sql =>
        """
        ALTER TABLE libraries ADD COLUMN default_policy_set_id TEXT NULL;

        CREATE INDEX IF NOT EXISTS ix_libraries_default_policy_set
            ON libraries (default_policy_set_id);
        """;
}
