using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Movies.Migrations;

/// <summary>
/// How well the subtitle Deluno holds actually fits the file.
///
/// <para><b>The column that stops the shelf lying.</b> Until now a subtitle was
/// a subtitle: the bar went green and nothing recorded whether the thing behind
/// it was cut for this release or for a different master forty seconds out.
/// James: <i>"we need the best method, no point spreading lies about subs that
/// may be out of sync etc etc."</i></para>
///
/// <para>Values are the rungs of <c>SubtitleMatch</c>: <c>0</c> any release,
/// <c>1</c> same source, <c>2</c> made for this file. Stored as the number
/// rather than a name because it is an ordered ladder and the ordering is the
/// point — "at or above the cutoff" has to be a comparison, not a lookup.</para>
///
/// <para><b>Defaulting to 0 is the honest default for what is already there.</b>
/// A subtitle fetched before this migration, or found beside the file, or read
/// out of the container, is a subtitle nobody checked the release of. Claiming
/// any of them matched would be inventing the very fact this column exists to
/// stop inventing — so they all read as "the right title, release unknown", and
/// the upgrade pass will find out.</para>
/// </summary>
public sealed class V0018MovieSubtitleMatch : SqliteSqlMigration
{
    public override int Version => 18;

    public override string Name => "movie_subtitle_match";

    protected override string Sql =>
        """
        ALTER TABLE movie_subtitle_state ADD COLUMN match_rung INTEGER NOT NULL DEFAULT 0;

        CREATE INDEX IF NOT EXISTS ix_movie_subtitle_state_match_rung
            ON movie_subtitle_state (match_rung);
        """;
}
