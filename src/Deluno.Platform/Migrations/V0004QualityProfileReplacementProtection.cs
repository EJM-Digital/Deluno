using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

public sealed class V0004QualityProfileReplacementProtection : SqliteSqlMigration
{
    public override int Version => 4;

    public override string Name => "quality_profile_replacement_protection";

    protected override string Sql =>
        """
        ALTER TABLE quality_profiles ADD COLUMN allow_lower_quality_replacements INTEGER DEFAULT 0;
        """;
}
