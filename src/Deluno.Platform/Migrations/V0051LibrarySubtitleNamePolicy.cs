using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// Stores the words a library's subtitles must or must not carry in their
/// release name. Empty remains the default, so an existing library keeps
/// accepting whatever its providers offer.
/// </summary>
public sealed class V0051LibrarySubtitleNamePolicy : SqliteSqlMigration
{
    public override int Version => 51;

    public override string Name => "library_subtitle_name_policy";

    protected override string Sql =>
        "ALTER TABLE libraries ADD COLUMN subtitle_name_policy_json TEXT NOT NULL DEFAULT '';";
}
