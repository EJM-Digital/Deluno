using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Media.Migrations;

/// <summary>
/// A title's own subtitle-language override (#843): a movie or a show can ask
/// for a different language list and mode than its library's default, which
/// this table records as a single row per title — never per file, per
/// season, or per episode, because the override is a decision about the
/// title, and a movie or show that stops asking for it goes back to the
/// library's own setting exactly as before, without anything left behind to
/// resurrect it by accident.
///
/// <para>The SQL body is shared by movies and series, the same as
/// <see cref="MediaTagsMigration"/> — only the closed map supplies the
/// database and entry table names.</para>
/// </summary>
public sealed class MediaSubtitleOverrideMigration(MediaKind kind, int version) : SqliteSqlMigration
{
    private readonly MediaTableMap map = MediaTableMap.For(kind);

    public override int Version => version;

    public override string Name => "media_subtitle_override";

    protected override string Sql => $"""
        CREATE TABLE IF NOT EXISTS {map.SubtitleOverrideTable} (
            {map.TagMediaIdColumn} TEXT NOT NULL PRIMARY KEY,
            languages TEXT NOT NULL,
            mode TEXT NOT NULL,
            updated_utc TEXT NOT NULL,
            FOREIGN KEY ({map.TagMediaIdColumn}) REFERENCES {map.EntryTable}(id) ON DELETE CASCADE
        );
        """;
}
