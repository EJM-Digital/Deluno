using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

public sealed class V0022LibraryDownloadClientCategories : SqliteSqlMigration
{
    public override int Version => 22;

    public override string Name => "library_download_client_categories";

    protected override string Sql =>
        """
        ALTER TABLE library_download_client_links ADD COLUMN category TEXT NULL;
        """;
}
