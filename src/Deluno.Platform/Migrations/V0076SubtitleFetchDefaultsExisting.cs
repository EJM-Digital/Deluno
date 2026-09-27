using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// #936 gives automatic subtitle fetching its own global switch
/// (<c>subtitles.fetchAutomatically</c>), asked once in the setup guide's
/// "What should Deluno do by itself?" step and set from then on in Settings
/// -> Subtitles. It reads a missing key as off, so a brand-new install needs
/// nothing written here — it already gets the off default #936 asks for.
///
/// <para>Before this switch existed, a library with subtitle languages
/// configured had its subtitles fetched automatically whenever the planner
/// ran, with nothing to turn that off short of clearing the language list.
/// An install that already had a library asking for subtitles was relying on
/// that behaviour, so this migration writes an explicit <c>true</c> for the
/// key if it is still absent — the same technique
/// <see cref="V0073AutomationDefaultsOff"/> used for renaming on import —
/// but only when the settings table already held something else first. A
/// database with no other setting row yet is a brand-new install and gets no
/// explicit write, matching the off default this switch is meant to ship
/// with.</para>
/// </summary>
public sealed class V0076SubtitleFetchDefaultsExisting : SqliteSqlMigration
{
    private const string SubtitleFetchKey = "subtitles.fetchAutomatically";

    public override int Version => 76;

    public override string Name => "subtitle_fetch_defaults_existing";

    protected override string Sql =>
        $"""
        INSERT INTO system_settings (setting_key, setting_value, updated_utc)
        SELECT '{SubtitleFetchKey}', 'true', datetime('now')
        WHERE NOT EXISTS (SELECT 1 FROM system_settings WHERE setting_key = '{SubtitleFetchKey}')
          AND EXISTS (
              SELECT 1 FROM system_settings
              WHERE setting_key != '{SubtitleFetchKey}'
          );
        """;
}
