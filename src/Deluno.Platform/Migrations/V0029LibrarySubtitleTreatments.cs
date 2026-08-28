using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// The two questions DESIGN-002 refused to guess at, given somewhere to be
/// answered.
///
/// <para><b>What a bare <c>Movie.srt</c> is.</b> DESIGN-002 deliberately records
/// a subtitle with no language in its name as <c>und</c> and counts it for
/// nothing — reading it as the library's first wanted language would be right
/// most of the time, and when it was wrong it would stop Deluno fetching a
/// language somebody asked for and never say why. That left a person with
/// <c>Movie.srt</c> beside every film looking at a wall of red.</para>
///
/// <para>Walking Bazarr (DESIGN-005) turned up the missing half: it does not
/// guess either, and it <i>asks once</i> — "treat unknown language subtitles
/// as…", empty by default. That is the whole answer. Empty here means <c>und</c>
/// counts for nothing, which is exactly today's behaviour, so an existing
/// install changes in no way at all.</para>
///
/// <para><b>Whether a track inside the container counts.</b> Deluno has always
/// counted an embedded track as held. Some people want a sidecar regardless,
/// because a player handles the two differently and an embedded track cannot be
/// swapped or corrected — Bazarr offers the same switch. Default true, which is
/// what Deluno already does.</para>
///
/// <para>Both are per library, for the reason every subtitle setting is: a
/// movie library and an anime library want different answers, and Deluno has
/// libraries where Bazarr has one global list.</para>
/// </summary>
public sealed class V0029LibrarySubtitleTreatments : SqliteSqlMigration
{
    public override int Version => 29;

    public override string Name => "library_subtitle_treatments";

    protected override string Sql =>
        """
        -- Empty means "do not guess", which is today's behaviour and the
        -- default. A language code here means "a subtitle with no language in
        -- its name is this one".
        ALTER TABLE libraries ADD COLUMN subtitle_unknown_language TEXT NOT NULL DEFAULT '';

        -- Deluno has always counted a track inside the container. Keeping that
        -- as the default means an existing install does not silently start
        -- fetching sidecars for files that already have the language.
        ALTER TABLE libraries ADD COLUMN subtitle_embedded_counts INTEGER NOT NULL DEFAULT 1;
        """;
}
