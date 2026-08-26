using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Jobs.Migrations;

/// <summary>
/// The other direction (#289).
///
/// Throughput was recorded as download only, from a time when downloading was
/// the only thing Deluno did with a torrent client. Now that it deliberately
/// holds files back so a site's sharing rule can be met (#288), "am I actually
/// seeding?" is a real question a user has and nothing on the dashboard could
/// answer it.
///
/// Defaulted rather than backfilled: readings taken before this column existed
/// genuinely did not measure upload, and writing a plausible zero over them
/// would be inventing history. A flat line at the left of the chart is the
/// truth about what Deluno knew then.
/// </summary>
public sealed class V0014ThroughputUploadSamples : SqliteSqlMigration
{
    public override int Version => 14;

    public override string Name => "throughput_upload_samples";

    protected override string Sql =>
        """
        ALTER TABLE download_throughput_samples ADD COLUMN upload_mbps REAL NOT NULL DEFAULT 0;
        """;
}
