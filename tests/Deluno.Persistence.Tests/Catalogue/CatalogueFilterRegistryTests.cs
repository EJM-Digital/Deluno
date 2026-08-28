using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Catalogue;

/// <summary>
/// The guard that makes a declared filter field safe to add.
///
/// <para>#324 turned nine hand-written properties into a registry, which trades
/// one risk for another: the old shape could not name a column that did not
/// exist, because somebody had to write the <c>WHERE</c> clause by hand. A
/// registry row can. So every field this declares is <b>executed</b> here,
/// against a real database, for both media kinds — a typo in a column name is a
/// failing test rather than a filter that quietly matches nothing.</para>
///
/// <para>That is the same failure #302 was deleted for, one layer down: a
/// vocabulary that could name things nothing answered, and no way to tell.</para>
/// </summary>
public sealed class CatalogueFilterRegistryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-27T00:00:00Z");

    [Fact]
    public async Task Every_declared_movie_filter_runs_against_the_real_schema()
    {
        using var storage = TestStorage.Create();
        var movies = await CreateMoviesAsync(storage);
        await movies.AddAsync(new CreateMovieRequest("Dune", 2021, null), CancellationToken.None);

        foreach (var filters in EveryCondition(MediaKind.Movie))
        {
            // Executing is the assertion. A column that does not exist is an
            // SqliteException here and a silently empty shelf in production.
            await movies.ListPageAsync(new CatalogueQuery(Filters: filters), CancellationToken.None);
        }
    }

    [Fact]
    public async Task Every_declared_series_filter_runs_against_the_real_schema()
    {
        using var storage = TestStorage.Create();
        var series = await CreateSeriesAsync(storage);
        await series.AddAsync(new CreateSeriesRequest("Severance", 2022, null), CancellationToken.None);

        foreach (var filters in EveryCondition(MediaKind.Series))
        {
            await series.ListPageAsync(new CatalogueQuery(Filters: filters), CancellationToken.None);
        }
    }

    /// <summary>
    /// Sonarr is not Radarr with seasons, and the registry has to say so. A film
    /// has three release dates; a show has a network. Offering either list on
    /// the other shelf is the wall of inert controls #324 exists to prevent.
    /// </summary>
    [Fact]
    public void The_two_kinds_do_not_offer_each_other_controls()
    {
        Assert.NotNull(CatalogueFilterFields.Find(MediaKind.Movie, "inCinemas"));
        Assert.Null(CatalogueFilterFields.Find(MediaKind.Series, "inCinemas"));

        Assert.NotNull(CatalogueFilterFields.Find(MediaKind.Series, "network"));
        Assert.Null(CatalogueFilterFields.Find(MediaKind.Movie, "network"));

        // And the shared ones really are one declaration, not two that agree today.
        var shared = CatalogueFilterFields.For(MediaKind.Movie)
            .Select(field => field.Id)
            .Intersect(CatalogueFilterFields.For(MediaKind.Series).Select(field => field.Id))
            .ToArray();

        Assert.Contains("quality", shared);
        Assert.Contains("genre", shared);
        Assert.Contains("releaseGroup", shared);
    }

    /// <summary>
    /// A condition naming something this kind cannot be asked is refused, never
    /// dropped. A silently ignored filter is a shelf that looks narrowed and is
    /// not, which is how somebody loses half their library and concludes Deluno
    /// has.
    /// </summary>
    [Fact]
    public void An_unanswerable_condition_is_refused_rather_than_ignored()
    {
        var parsed = CatalogueFilters.Parse(MediaKind.Series, ["inCinemas:before:2020-01-01"], out var errors);

        Assert.True(parsed.IsEmpty);
        Assert.Single(errors);

        CatalogueFilters.Parse(MediaKind.Movie, ["studio:is:A24"], out var unknown);
        Assert.Single(unknown);

        // An operator the field's value kind does not carry is the same class of
        // mistake: "quality starts with" is not a question the ladder can answer.
        CatalogueFilters.Parse(MediaKind.Movie, ["quality:starts:Rem"], out var wrongOperator);
        Assert.Single(wrongOperator);
    }

    /// <summary>
    /// The round trip a saved view depends on. Values carry pipes, not commas,
    /// and the split takes only the first two colons so a Windows path survives.
    /// </summary>
    [Fact]
    public void A_condition_survives_the_query_string()
    {
        var condition = new CatalogueFilterCondition(
            "path", CatalogueFilterOperator.StartsWith, [@"D:\Media\Films"]);

        var parsed = CatalogueFilters.Parse(MediaKind.Movie, [condition.Encode()], out var errors);

        Assert.Empty(errors);
        var round = Assert.Single(parsed.Conditions!);
        Assert.Equal(condition.FieldId, round.FieldId);
        Assert.Equal(condition.Operator, round.Operator);
        // The value still holds its drive letter: the split takes the first two
        // colons only, and a path is the reason that matters.
        Assert.Equal(condition.Values, round.Values);
    }

    /// <summary>
    /// The two lists that have to agree: what the toolbar offers to order by,
    /// and what the paged query can actually perform. They were a browser array
    /// and a server constant until #324, which is the shape every defect in this
    /// codebase has had.
    /// </summary>
    [Fact]
    public void Every_offered_sort_is_one_the_query_can_perform()
    {
        foreach (var kind in new[] { MediaKind.Movie, MediaKind.Series })
        {
            var offered = CatalogueControls.For(kind).SortFields.Select(sort => sort.Id).ToArray();

            // Per kind, not one list for both: a film has no next episode and no
            // network, and a sort that can only ever do nothing is the failure
            // #324 was opened about.
            Assert.Equal(CatalogueSortFields.ForKind(kind).Order(), offered.Order());
            Assert.Equal(offered.Length, offered.Distinct().Count());
        }
    }

    /// <summary>
    /// Nothing declared may be unaskable, and nothing askable may be undeclared:
    /// every field carries at least one operator and every operator has a token
    /// it travels as.
    /// </summary>
    [Fact]
    public void Every_field_can_be_asked_and_every_operator_can_be_written()
    {
        foreach (var kind in new[] { MediaKind.Movie, MediaKind.Series })
        {
            var fields = CatalogueFilterFields.For(kind);
            Assert.Equal(fields.Count, fields.Select(field => field.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());

            foreach (var field in fields)
            {
                Assert.NotEmpty(field.Operators);
                Assert.NotEmpty(field.Label);
                Assert.NotEmpty(field.Hint);
                // An entry field is written against the catalogue's own alias,
                // which differs between the two catalogues; a wanted-state field
                // reads `ws`, the one row the page speaks for. Anything else is
                // a column nobody substituted.
                Assert.Contains(
                    field.Source == CatalogueFilterSource.Entry ? "{alias}" : "ws.",
                    field.Column,
                    StringComparison.Ordinal);

                foreach (var op in field.Operators)
                {
                    Assert.False(string.IsNullOrWhiteSpace(CatalogueFilterOperators.Token(op)));
                }
            }
        }
    }

    /* ------------------------------------------------------------ helpers */

    /// <summary>
    /// One filter set per field-and-operator pair the registry declares. A
    /// plausible value per value kind, because the point is that the SQL runs,
    /// not what it returns.
    /// </summary>
    private static IEnumerable<CatalogueFilters> EveryCondition(MediaKind kind)
    {
        foreach (var field in CatalogueFilterFields.For(kind))
        {
            foreach (var op in field.Operators)
            {
                var values = CatalogueFilterOperators.TakesValues(op) ? new[] { SampleValue(field, op) } : [];
                yield return CatalogueFilters.Of(new CatalogueFilterCondition(field.Id, op, values));
            }
        }
    }

    private static string SampleValue(CatalogueFilterField field, CatalogueFilterOperator op)
        => op is CatalogueFilterOperator.WithinLastDays or CatalogueFilterOperator.MoreThanDaysAgo
            ? "30"
            : field.ValueKind switch
            {
                CatalogueFilterValueKind.Year => "2000",
                CatalogueFilterValueKind.Minutes => "120",
                CatalogueFilterValueKind.Gigabytes => "5",
                CatalogueFilterValueKind.Rating => "7.5",
                CatalogueFilterValueKind.Decimal => "1",
                CatalogueFilterValueKind.Integer => "10",
                CatalogueFilterValueKind.Boolean => "true",
                CatalogueFilterValueKind.Date => "2024-01-01T00:00:00.0000000Z",
                CatalogueFilterValueKind.Enum => field.Options?[0] ?? "x",
                _ => "sample"
            };

    private static async Task<SqliteMovieCatalogRepository> CreateMoviesAsync(TestStorage storage)
    {
        var timeProvider = new FixedTimeProvider(Now);
        await new MoviesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        return new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
    }

    private static async Task<SqliteSeriesCatalogRepository> CreateSeriesAsync(TestStorage storage)
    {
        var timeProvider = new FixedTimeProvider(Now);
        await new SeriesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        return new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
    }
}
