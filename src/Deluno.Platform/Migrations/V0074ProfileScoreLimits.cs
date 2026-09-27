using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// Lets a profile hold the TRaSH-style score guardrails behind the plain
/// choices: "Stop upgrading once a copy is", "Only replace when clearly
/// better" and "Never grab copies rated bad" (#778).
///
/// <para>The TRaSH profile definitions Deluno ships and syncs from already
/// carry <c>minFormatScore</c>, <c>cutoffFormatScore</c> and
/// <c>minUpgradeFormatScore</c>, but nothing read them, so a profile had no
/// minimum, no cutoff and no upgrade-gain floor of its own. Empty means the
/// profile has no score opinion, which is exactly what every profile had
/// before it could hold one - a migrated install's behaviour does not
/// silently change.</para>
/// </summary>
public sealed class V0074ProfileScoreLimits : SqliteSqlMigration
{
    public override int Version => 74;

    public override string Name => "profile_score_limits";

    protected override string Sql =>
        "ALTER TABLE quality_profiles ADD COLUMN score_limits_json TEXT NOT NULL DEFAULT '';";
}
