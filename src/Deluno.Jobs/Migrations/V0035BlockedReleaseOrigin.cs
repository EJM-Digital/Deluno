using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Jobs.Migrations;

/// <summary>
/// Whether a block was a person's decision or Deluno's own (#800). Sonarr
/// says this explicitly on its blocklist entries; Deluno's history was silent
/// about it, though the two call sites that ever set it manually already
/// existed before this and used the same reason code for it.
///
/// <para>A row from before this migration was never given a choice, so it
/// defaults to "automatic" — the more common origin, and the safer guess:
/// nothing here claims a person did something they did not.</para>
/// </summary>
public sealed class V0035BlockedReleaseOrigin : SqliteSqlMigration
{
    public override int Version => 35;

    public override string Name => "blocked_release_origin";

    protected override string Sql =>
        "ALTER TABLE blocked_releases ADD COLUMN origin TEXT NOT NULL DEFAULT 'automatic';";
}
