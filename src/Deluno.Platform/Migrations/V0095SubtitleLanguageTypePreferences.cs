using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// Per-language subtitle-type preferences: hearing-impaired
/// required/not-required/only, and whether a forced track is fetched as an
/// extra file (#835, #836, #923).
///
/// One JSON column, the same shape as <c>subtitle_content_policy_json</c>
/// beside it: a per-title-language row would be one more table for a setting
/// that a handful of languages ever carries, and the codec that reads it
/// already treats a missing or unreadable value as "nothing set, use the
/// defaults everybody had before this existed".
/// </summary>
public sealed class V0095SubtitleLanguageTypePreferences : SqliteSqlMigration
{
    public override int Version => 95;

    public override string Name => "subtitle_language_type_preferences";

    protected override string Sql =>
        """
        ALTER TABLE libraries ADD COLUMN subtitle_language_type_preferences_json TEXT NULL;
        """;
}
