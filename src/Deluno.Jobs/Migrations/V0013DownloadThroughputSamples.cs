using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Jobs.Migrations;

/// <summary>
/// A short rolling history of download throughput.
///
/// Deluno stored no measurement of speed anywhere — only counts of stored rows.
/// The dashboard's live wave was therefore a browser-side window that started
/// empty every time the page was opened and vanished when it was closed, which
/// answers "what is it doing right now" but never "was it slow overnight".
///
/// Samples are small and deliberately short-lived: one row a minute is about
/// 1,440 a day, and the sampler prunes past its retention window, so this
/// cannot grow without bound the way an event log would.
/// </summary>
public sealed class V0013DownloadThroughputSamples : SqliteSqlMigration
{
    public override int Version => 13;

    public override string Name => "download_throughput_samples";

    protected override string Sql =>
        """
        CREATE TABLE IF NOT EXISTS download_throughput_samples (
            captured_utc TEXT PRIMARY KEY,
            speed_mbps REAL NOT NULL,
            active_count INTEGER NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_download_throughput_samples_captured
            ON download_throughput_samples (captured_utc DESC);
        """;
}
