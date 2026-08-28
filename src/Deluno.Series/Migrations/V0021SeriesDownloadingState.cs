using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Series.Migrations;

/// <summary>
/// When a title was handed to a download client, so the shelf can say so — and
/// so it can stop saying so on its own if nothing ever tells it the download
/// ended.
///
/// <para><b>The state itself needs no column.</b> <c>wanted_status</c> is
/// already what the mark, the filters and the counts above the grid are all
/// computed from, and it is already indexed. <c>downloading</c> is a value of
/// it, not a fifth thing to keep in step with the other four.</para>
///
/// <para><b>This column is the safety net.</b> Excluding <c>downloading</c> from
/// the work list is the point of the state — it is what stops Deluno grabbing
/// the same release twice — and it is also the danger. If a dispatch dies
/// because the client was removed, the torrent stalled out, or Deluno restarted
/// mid-flight, and nothing rewrites the status, the title sits on
/// <c>downloading</c> for ever and is <b>silently never searched again</b>. No
/// error, nothing on screen, and the only symptom is an absence of
/// activity.</para>
///
/// <para>That is the shape of the two worst defects this project has had: the
/// release-search switches that starved subtitles for a whole session, and the
/// subtitle somebody deleted that Deluno never noticed. Both were invisible
/// because nothing failed.</para>
///
/// <para>So the moment is recorded, and a status that has sat here too long
/// stops counting — the same reasoning as <c>V0020SeriesProgressFacts</c>, which
/// stores its expiry beside its answer. The poll is what should clear it; this
/// is what happens when the poll never comes.</para>
/// </summary>
public sealed class V0021SeriesDownloadingState : SqliteSqlMigration
{
    public override int Version => 21;

    public override string Name => "series_downloading_state";

    protected override string Sql =>
        """
        -- No index. ix_series_wanted_state_library_status already covers the
        -- work-list query, and a second one on (wanted_status,
        -- downloading_since_utc) made SQLite prefer it for the catalogue page's
        -- correlated pick, taking a page at twenty thousand titles from
        -- milliseconds to 13.4 seconds on the movies side.
        ALTER TABLE series_wanted_state ADD COLUMN downloading_since_utc TEXT NULL;

        """;
}
