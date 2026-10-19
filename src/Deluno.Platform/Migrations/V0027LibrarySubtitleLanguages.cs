using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// Which subtitle languages a library wants, and how many of them it needs.
///
/// Per library, because that is what Deluno has and Bazarr and Weir do not:
/// one global list cannot say "English on everything, Japanese on anime". It
/// sits beside <c>quality_profile_id</c> and <c>cutoff_quality</c>, where "what
/// I want for this shelf" already lives. See DESIGN-002 and
/// #301.
///
/// <c>subtitle_language_mode</c> is the simplification of Bazarr's ordered list
/// plus a cutoff index, which conflates two different intentions:
///
///   <c>all</c>   — every language listed. "English *and* Japanese."
///   <c>first</c> — the first one obtainable, in order. "English, or Spanish
///                  if English cannot be found; do not fetch both."
///
/// Two plain words instead of a position in a list, and the difference is what
/// the bar under a poster counts: <c>all</c> wants every language per file,
/// <c>first</c> wants exactly one.
///
/// Empty is the default and means no subtitles are wanted, which draws no bar.
/// </summary>
public sealed class V0027LibrarySubtitleLanguages : SqliteSqlMigration
{
    public override int Version => 27;

    public override string Name => "library_subtitle_languages";

    protected override string Sql =>
        """
        ALTER TABLE libraries ADD COLUMN subtitle_languages TEXT NOT NULL DEFAULT '';
        ALTER TABLE libraries ADD COLUMN subtitle_language_mode TEXT NOT NULL DEFAULT 'all';
        """;
}
