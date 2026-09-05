using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Jobs.Migrations;

/// <summary>
/// When a blocked release's leftovers were cleared from the download client.
///
/// <para>Null means "still to do". The clearing itself cannot happen at the
/// moment of blocking, because the sharing rule may still own that copy — so
/// the intent is recorded here and a worker pass acts on it once the rule
/// allows. DESIGN-007 decisions 16 and 17.</para>
/// </summary>
public sealed class V0027BlockedReleaseCleanup : SqliteSqlMigration
{
    public override int Version => 27;

    public override string Name => "blocked_release_cleanup";

    protected override string Sql =>
        "ALTER TABLE blocked_releases ADD COLUMN cleaned_up_utc TEXT NULL;";
}
