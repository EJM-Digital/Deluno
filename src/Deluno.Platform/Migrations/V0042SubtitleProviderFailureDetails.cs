using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>Stores the typed failure behind the latest subtitle-provider health result.</summary>
public sealed class V0042SubtitleProviderFailureDetails : SqliteSqlMigration
{
    public override int Version => 42;

    public override string Name => "subtitle_provider_failure_details";

    protected override string Sql =>
        "ALTER TABLE subtitle_providers ADD COLUMN last_health_failure_json TEXT NULL;";
}
