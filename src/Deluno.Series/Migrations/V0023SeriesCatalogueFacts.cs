using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Media;

namespace Deluno.Series.Migrations;

/// <summary>
/// The same six title facts and four ratings a film gets, for a show.
///
/// <para>The body is <see cref="CatalogueFactsMigrationSql"/>, shared with the
/// movie migration of the same name. A question worth asking of one shelf is
/// worth asking of the other, and generating both from one string is what stops
/// the two drifting the way <c>network</c> did.</para>
/// </summary>
public sealed class V0023SeriesCatalogueFacts : SqliteSqlMigration
{
    public override int Version => 23;

    public override string Name => "series_catalogue_facts";

    protected override string Sql => CatalogueFactsMigrationSql.For("series_entries", "series_entries");
}
