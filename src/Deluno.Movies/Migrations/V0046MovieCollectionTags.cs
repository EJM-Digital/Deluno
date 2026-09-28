using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Movies.Migrations;

/// <summary>
/// #1046: a collection can give tags to every movie it adds (Radarr's own
/// collection tags). Empty for every existing collection, so nothing changes
/// until someone chooses tags.
/// </summary>
public sealed class V0046MovieCollectionTags : SqliteSqlMigration
{
    public override int Version => 46;

    public override string Name => "movie_collection_tags";

    protected override string Sql =>
        """
        ALTER TABLE movie_collections ADD COLUMN tags TEXT NOT NULL DEFAULT '';
        """;
}
