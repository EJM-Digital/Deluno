using System.Globalization;
using System.Text;

namespace Deluno.Media;

/// <summary>
/// When Deluno last read a file's own streams, and how big it was when it did.
///
/// <para><b>Why this pass has its own bookkeeping.</b> Reading the codec was
/// briefly hung off the subtitle scan, because that pass already runs ffprobe
/// and opening the file twice looked wasteful. James: <i>"dont you think its
/// better we separate these jobs so nothing relies on each other or fights or
/// conflicts or overlaps... everything needs to run independently"</i>. He is
/// right, and the coupling had already produced a real defect: the subtitle
/// scan returns immediately for a library that asks for no subtitle languages,
/// so turning subtitles off would have silently stopped codecs being read.</para>
///
/// <para>One saved file read is not worth a pass that only works while another,
/// unrelated feature is switched on. These two columns are what let the media
/// probe decide for itself what it still owes, with nothing to ask anyone
/// else.</para>
///
/// <para><b>Size is the change detector.</b> A path can be rewritten in place
/// by a repack or an upgrade; comparing the size at probe time against the size
/// now catches that without stat-ing every file in the library on every pass.
/// The same trick the subtitle scan uses on its own table, which is why that
/// one is not shared — it is a small idea, not a shared dependency.</para>
/// </summary>
public static class CatalogueFileProbeMigrationSql
{
    public static string For(string wantedTable, string indexPrefix)
    {
        var sql = new StringBuilder();

        sql.AppendLine(CultureInfo.InvariantCulture, $"ALTER TABLE {wantedTable} ADD COLUMN facts_probed_utc TEXT NULL;");
        sql.AppendLine(CultureInfo.InvariantCulture, $"ALTER TABLE {wantedTable} ADD COLUMN facts_probed_size_bytes INTEGER NULL;");
        sql.AppendLine();

        // Partial: the pass only ever asks for rows that hold a file, and an
        // index over the rest is weight every lookup carries for nothing.
        sql.AppendLine(CultureInfo.InvariantCulture, $"""
            CREATE INDEX IF NOT EXISTS ix_{indexPrefix}_file_probe_due
                ON {wantedTable} (facts_probed_utc)
                WHERE has_file = 1 AND file_path IS NOT NULL;
            """);

        return sql.ToString();
    }
}
