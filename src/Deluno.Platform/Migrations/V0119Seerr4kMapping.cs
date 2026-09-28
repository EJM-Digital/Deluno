using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// #985: Deluno is built for multiple libraries, so a 4K Seerr request goes
/// to its own library and quality profile instead of the normal one.
///
/// - seerr_servers gains a second, optional library/quality-profile mapping
///   per media type for 4K requests, plus a switch for what happens when a
///   4K request arrives and no 4K mapping is set: refuse it (the default,
///   written to Activity in plain words) or fall back to the normal library.
///   Every existing server gets NULL 4K mappings and the fallback switch off,
///   so nothing changes for an existing connection until it is set on the
///   card.
/// - seerr_acquisitions gains who asked, so the Seerr card's recent
///   acquisitions can show it.
/// </summary>
public sealed class V0119Seerr4kMapping : SqliteSqlMigration
{
    public override int Version => 119;

    public override string Name => "seerr_4k_mapping";

    protected override string Sql =>
        """
        ALTER TABLE seerr_servers ADD COLUMN movie_4k_library_id TEXT NULL;
        ALTER TABLE seerr_servers ADD COLUMN movie_4k_quality_profile_id TEXT NULL;
        ALTER TABLE seerr_servers ADD COLUMN series_4k_library_id TEXT NULL;
        ALTER TABLE seerr_servers ADD COLUMN series_4k_quality_profile_id TEXT NULL;
        ALTER TABLE seerr_servers ADD COLUMN fallback_4k_to_normal_library INTEGER NOT NULL DEFAULT 0;

        ALTER TABLE seerr_acquisitions ADD COLUMN requested_by TEXT NULL;
        """;
}
