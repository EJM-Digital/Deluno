using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Jobs.Migrations;

/// <summary>
/// Gives subtitle work its own cursor, because it is not a search.
///
/// <para>Subtitle scanning and fetching were planned inside the release-search
/// branch, so they inherited its two on/off switches along with its cycle. A
/// library with "Search automatically" turned off — which that screen describes
/// as <i>keep this library manual</i>, meaning manual <i>releases</i> — asked
/// for English every day and was never given it, and said nothing. So did a
/// library with searching on but both missing and upgrade off.</para>
///
/// <para>That is the audience Bazarr is built for: a library that is already
/// complete and wants subtitles for it. Deluno was refusing exactly them.</para>
///
/// <para>The cycle, the window and the manual override stay shared — DESIGN-002
/// rule 3, no second scheduler. What separates is only the clock and the reason
/// to run, which is whether the library asked for any languages.</para>
///
/// <para><c>next_search_utc</c> deliberately does not fold this in: it is what
/// the automation screen prints as the next search, and a subtitle pass never
/// reaches an indexer.</para>
/// </summary>
public sealed class V0018LibrarySubtitleSchedule : SqliteSqlMigration
{
    public override int Version => 18;

    public override string Name => "library_subtitle_schedule";

    protected override string Sql =>
        """
        ALTER TABLE library_automation_state ADD COLUMN next_subtitle_search_utc TEXT NULL;

        CREATE INDEX IF NOT EXISTS ix_library_automation_state_next_subtitle_search
            ON library_automation_state (next_subtitle_search_utc);
        """;
}
