using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// #1292: IMDb answers programs with a bot check (HTTP 202, empty body), so an IMDb import list can never be
/// read and the provider is gone. A saved IMDb list is never deleted: it is switched off and carries one plain
/// note, shown under its sync column on the Lists screen, saying what to use instead.
/// </summary>
public sealed class V0139IntakeImdbListsRetired : SqliteSqlMigration
{
    public override int Version => 139;

    public override string Name => "intake_imdb_lists_retired";

    protected override string Sql =>
        """
        UPDATE intake_sources
        SET is_enabled = 0,
            last_sync_status = 'error',
            last_sync_summary = 'IMDb lists are no longer supported because IMDb blocks programs; use MDBList or Trakt to import an IMDb list.',
            updated_utc = datetime('now')
        WHERE lower(provider) = 'imdb';
        """;
}
