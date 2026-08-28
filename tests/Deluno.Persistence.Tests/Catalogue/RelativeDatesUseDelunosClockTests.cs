using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Catalogue;

/// <summary>
/// A relative date filter resolves against <b>Deluno's</b> clock, not the wall
/// clock of whatever machine happens to be running the query.
///
/// <para><b>Why this is not the same test as
/// <see cref="RelativeDateFilterTests"/>.</b> That one proves the binder does
/// the arithmetic at query time rather than at save time, and it passes the
/// clock in by hand. It would go on passing if no caller ever supplied one —
/// which is exactly what was happening: both catalogue repositories called
/// <c>BindCustomFilters</c> without their <c>TimeProvider</c>, so the binder
/// fell through to its <c>DateTimeOffset.UtcNow</c> default and every relative
/// filter in the product quietly ignored the clock Deluno was told to use.</para>
///
/// <para>Invisible in production, because the two agree there. Visible the
/// moment anything else depends on the clock — a test with a fixed one, and any
/// future replay or scheduling work that moves it.</para>
/// </summary>
public sealed class RelativeDatesUseDelunosClockTests
{
    [Fact]
    public async Task A_relative_filter_answers_to_the_clock_Deluno_was_given()
    {
        using var storage = TestStorage.Create();

        // Deliberately ahead of any wall clock this will ever run under. With a
        // date in the past the test passes either way — the film is old by both
        // clocks — and a guard that cannot fail is decoration.
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2099-03-01T00:00:00Z"));
        var movies = await CreateAsync(storage, clock);

        await movies.AddAsync(new CreateMovieRequest("Arrival", 2016, null), CancellationToken.None);

        // Two months pass, by Deluno's clock and nobody else's. The wall clock
        // has moved by however long this test takes to run.
        clock.Advance(TimeSpan.FromDays(60));

        var recent = await movies.ListPageAsync(
            new CatalogueQuery(Filters: new CatalogueFilters(
                [new CatalogueFilterCondition("added", CatalogueFilterOperator.WithinLastDays, ["30"])])),
            CancellationToken.None);

        // The film was added two months ago by Deluno's reckoning, so it is not
        // recent. If the binder is on the wall clock instead, it was added
        // moments ago and comes straight back — which is what happened before
        // the repositories started handing their TimeProvider over.
        Assert.Empty(recent.Items);

        var everything = await movies.ListPageAsync(
            new CatalogueQuery(Filters: new CatalogueFilters(
                [new CatalogueFilterCondition("added", CatalogueFilterOperator.MoreThanDaysAgo, ["30"])])),
            CancellationToken.None);

        Assert.Single(everything.Items);
    }

    private static async Task<SqliteMovieCatalogRepository> CreateAsync(TestStorage storage, TimeProvider clock)
    {
        await new MoviesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        return new SqliteMovieCatalogRepository(storage.Factory, clock);
    }
}
