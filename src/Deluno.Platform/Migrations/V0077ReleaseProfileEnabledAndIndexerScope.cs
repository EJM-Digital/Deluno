using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// Lets a release profile be turned off without deleting it, and scoped to
/// one or more indexers instead of always applying across every indexer
/// (#770). Empty indexer scope means every indexer, matching today's only
/// behaviour; enabled defaults true so an existing profile keeps acting
/// exactly as it did before either column existed.
/// </summary>
public sealed class V0077ReleaseProfileEnabledAndIndexerScope : SqliteSqlMigration
{
    public override int Version => 77;

    public override string Name => "release_profile_enabled_and_indexer_scope";

    protected override string Sql =>
        """
        ALTER TABLE release_profiles ADD COLUMN enabled INTEGER NOT NULL DEFAULT 1;
        ALTER TABLE release_profiles ADD COLUMN indexer_ids_json TEXT NOT NULL DEFAULT '[]';
        """;
}
