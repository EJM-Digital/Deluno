using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// #764 gives folder renaming its own switch, separate from the existing
/// file-rename switch (<c>media.renameOnImport</c>). Before this, the
/// movie/series destination folder was always computed from the naming
/// template -- there was no way to keep a release's own folder name. #936
/// says a brand-new install reads the new switch's missing key as off (the
/// release folder is kept as-is when there was one to keep), but an install
/// that already existed before this migration ran must keep exactly what it
/// was already doing: the folder was always renamed to the template.
///
/// <para>This is the same shape as
/// <see cref="V0073AutomationDefaultsOff"/>: write an explicit
/// <c>true</c> for <c>media.renameFoldersOnImport</c> only for a database
/// that already held some other setting first (an install that has actually
/// been configured), and only when the key is not already present. A
/// database with no other setting row yet is brand new and gets no explicit
/// write, so it reads off -- exactly what a new install should read.</para>
/// </summary>
public sealed class V0084RenameFoldersOnImportDefaultsExisting : SqliteSqlMigration
{
    private const string RenameFoldersOnImportKey = "media.renameFoldersOnImport";

    public override int Version => 84;

    public override string Name => "rename_folders_on_import_defaults_existing";

    protected override string Sql =>
        $"""
        INSERT INTO system_settings (setting_key, setting_value, updated_utc)
        SELECT '{RenameFoldersOnImportKey}', 'true', datetime('now')
        WHERE NOT EXISTS (SELECT 1 FROM system_settings WHERE setting_key = '{RenameFoldersOnImportKey}')
          AND EXISTS (
              SELECT 1 FROM system_settings
              WHERE setting_key != '{RenameFoldersOnImportKey}'
          );
        """;
}
