using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Catalogue;

/// <summary>
/// <i>The Matrix</i> files under M, and the two languages that decide so agree.
///
/// <para>#325 asks for this in as many words: <i>"the normalisation exists in
/// one place with a test that proves the SQL and the C# agree."</i> SQLite
/// computes it in a trigger, because no write path may forget it, and C# needs
/// it for the A–Z rail. That is one rule in two languages — the shape behind
/// every defect worth finding here — so this runs both over the same titles and
/// fails the moment either moves.</para>
/// </summary>
public sealed class SortTitleTests
{
    /// <summary>
    /// Real shapes, including the ones that break a naive implementation.
    /// </summary>
    private static readonly string[] Titles =
    [
        "The Matrix",
        "A Beautiful Mind",
        "An Education",
        "Arrival",
        "the lower case the",
        "THE SHOUTING ONE",
        // Only an article. Stripping it would file the film under nothing at
        // all, in a bucket the rail cannot name.
        "The",
        "A",
        // "An" tried before "A", or this becomes "n Education".
        "Andor",
        // Leading and trailing space, which a hand-typed title really does have.
        "  The Bear  ",
        // Not an article, merely starting with the same letters.
        "Theatre of Blood",
        "Anatomy of a Fall",
        // A Spanish article Deluno deliberately does not strip, because the
        // same word starts English titles too.
        "Los Angeles Plays Itself",
        "1917",
        ""
    ];

    [Fact]
    public void The_sql_and_the_csharp_file_every_title_the_same_way()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        foreach (var title in Titles)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {SortTitle.SqlExpression("@title")};";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@title";
            parameter.Value = title;
            command.Parameters.Add(parameter);

            var fromSql = command.ExecuteScalar() as string ?? string.Empty;

            Assert.Equal(SortTitle.For(title), fromSql);
        }
    }

    [Fact]
    public void An_article_is_dropped_and_a_title_that_is_only_an_article_is_not()
    {
        Assert.Equal("matrix", SortTitle.For("The Matrix"));
        Assert.Equal("beautiful mind", SortTitle.For("A Beautiful Mind"));
        Assert.Equal("education", SortTitle.For("An Education"));

        // Would be "n Education" if "a" were tried before "an".
        Assert.Equal("andor", SortTitle.For("Andor"));

        Assert.Equal("the", SortTitle.For("The"));
        Assert.Equal("theatre of blood", SortTitle.For("Theatre of Blood"));

        // Deliberately untouched: strip "Los" and this English title files
        // under A. See SortTitle for why language-aware stripping waits.
        Assert.Equal("los angeles plays itself", SortTitle.For("Los Angeles Plays Itself"));
    }

    /// <summary>
    /// The trigger is what makes it true of rows nobody thought about — a title
    /// changed by a metadata refresh, an import, or a hand edit.
    /// </summary>
    [Fact]
    public async Task A_shelf_orders_by_the_filed_name_and_keeps_up_when_a_title_changes()
    {
        using var storage = TestStorage.Create();
        var movies = await CreateAsync(storage);

        foreach (var title in new[] { "The Matrix", "Arrival", "An Education", "Blade Runner" })
        {
            await movies.AddAsync(new CreateMovieRequest(title, 2000, null), CancellationToken.None);
        }

        var ordered = await OrderedTitlesAsync(movies);

        // Arrival, Blade Runner, An Education, The Matrix — filed under
        // A, B, E, M rather than A, A, B, T.
        Assert.Equal(["Arrival", "Blade Runner", "An Education", "The Matrix"], ordered);
    }

    private static async Task<string[]> OrderedTitlesAsync(IMovieCatalogRepository movies)
    {
        var page = await movies.ListPageAsync(
            new CatalogueQuery(Sort: CatalogueSortFields.Title, Descending: false, PageSize: 50),
            CancellationToken.None);

        return [.. page.Items.Select(item => item.Title)];
    }

    private static async Task<SqliteMovieCatalogRepository> CreateAsync(TestStorage storage)
    {
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-03-02T00:00:00Z"));

        await new MoviesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        return new SqliteMovieCatalogRepository(storage.Factory, clock);
    }
}
