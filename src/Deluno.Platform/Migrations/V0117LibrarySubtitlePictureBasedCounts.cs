using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// #988 (parity decision sheet row P05): a switch so an embedded picture-only
/// subtitle track (PGS/VobSub) does not count as already holding a language,
/// so a text subtitle is still fetched for it. Bazarr calls this ignoring
/// embedded PGS/VobSub subtitles.
///
/// <para>Defaults to <c>1</c> (counts, same as any other embedded track) for
/// both a new install and an existing one — the same column
/// <c>subtitle_embedded_counts</c> beside it already defaults to, and exactly
/// what Deluno did before this switch existed. #936 asks every new automatic
/// behaviour to start off; this is not automatic at all, only a stricter
/// reading of an existing preference, so the one default that satisfies both
/// "off for a new install" and "unchanged for an existing one" is the same
/// default: keep counting them.</para>
/// </summary>
public sealed class V0117LibrarySubtitlePictureBasedCounts : SqliteSqlMigration
{
    public override int Version => 117;

    public override string Name => "library_subtitle_picture_based_counts";

    protected override string Sql =>
        """
        ALTER TABLE libraries ADD COLUMN subtitle_picture_based_counts INTEGER NOT NULL DEFAULT 1;
        """;
}
