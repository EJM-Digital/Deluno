using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// #1046: tags a source gives to the titles it adds. A Seerr connection can
/// name tags for every title it adds, and extra ones for a 4K request; both
/// start empty, so an existing connection behaves exactly as before. A list
/// title also remembers when a sync last found it missing from its list
/// (dropped_utc), which is how the optional "tag titles that are no longer on
/// any list" setting knows a title has left every list. The setting itself is
/// a system_settings key that is absent (off) until someone turns it on, so
/// no install, new or existing, starts with it on (#936).
/// </summary>
public sealed class V0123SeerrTagsAndListDrops : SqliteSqlMigration
{
    public override int Version => 123;

    public override string Name => "seerr_tags_and_list_drops";

    protected override string Sql =>
        """
        ALTER TABLE seerr_servers ADD COLUMN tags TEXT NOT NULL DEFAULT '';
        ALTER TABLE seerr_servers ADD COLUMN tags_4k TEXT NOT NULL DEFAULT '';
        ALTER TABLE intake_title_origins ADD COLUMN dropped_utc TEXT NULL;
        """;
}
