using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// Download-health records move to a new key under a new strike rule (#560).
///
/// <para>The first rule counted sightings more than half an hour apart, never
/// reset, and followed a torrent's infohash from one download to the next. The
/// records it left say "three strikes" about downloads that stalled for a few
/// seconds at the start of healthy runs, and they blocked those releases.
/// Their start times are the first sighting ever, not the start of a problem,
/// so they cannot be converted. They are removed, and the new rule builds its
/// own records from the next snapshot.</para>
/// </summary>
public sealed class V0066DownloadHealthStrikesV2 : SqliteSqlMigration
{
    public override int Version => 66;

    public override string Name => "download_health_strikes_v2";

    protected override string Sql =>
        """
        DELETE FROM system_settings WHERE setting_key = 'download-health.records.v1';
        """;
}
