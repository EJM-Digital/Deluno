using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// Lets a library ask for subtitles named after the video and nothing else.
///
/// <para>Some players — mostly older televisions — only load a subtitle whose
/// name is exactly the video's name with <c>.srt</c> on the end, and ignore
/// <c>Film.en.srt</c> entirely. That is a real shelf somebody owns, and the
/// alternative was renaming every file by hand after each fetch. Off by
/// default: the name it produces no longer says what language the file is.</para>
/// </summary>
public sealed class V0052LibrarySubtitleSingleLanguageName : SqliteSqlMigration
{
    public override int Version => 52;

    public override string Name => "library_subtitle_single_language_name";

    protected override string Sql =>
        "ALTER TABLE libraries ADD COLUMN subtitle_omit_language_code INTEGER NOT NULL DEFAULT 0;";
}
