using Deluno.Media;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Series;

/// <summary>
/// Series status and network survive a metadata refresh, and the shelf can
/// actually be narrowed by them.
///
/// <para><b>Both were columns nobody wrote.</b> <c>network</c> has been on
/// <c>series_entries</c> since V0012 and had no writer at all, so the Network
/// filter matched nothing from the day it was offered. <c>status</c> arrived
/// with V0020 and had the same hole: backfilled once by its own migration and
/// then never maintained, so every refresh after it left the column
/// behind.</para>
///
/// <para><b>Invisible in the way that matters.</b> A filter over an empty column
/// returns no rows, and no rows looks like a perfectly fair answer — which is
/// how it survived on the rig long enough to be found by asking for shows that
/// had ended and being told there were none.</para>
///
/// <para>So these assert through <c>ListPageAsync</c> rather than by reading the
/// column back. A test that read the column would prove the write happened and
/// say nothing about whether anybody can use it.</para>
/// </summary>
public sealed class SeriesMetadataFacetsTests
{
    [Fact]
    public async Task A_refresh_records_what_a_show_is_and_who_broadcasts_it()
    {
        using var storage = TestStorage.Create();
        var series = await CreateAsync(storage);

        var id = await AddAsync(series, "Severance");
        await RefreshAsync(series, id, status: "Returning Series", network: "Apple TV+");

        Assert.Equal(1, await CountAsync(series, "seriesStatus", CatalogueFilterOperator.Includes, "Returning Series"));
        Assert.Equal(1, await CountAsync(series, "network", CatalogueFilterOperator.Is, "Apple TV+"));

        // And it is a real narrowing rather than a filter that matches whatever
        // it is handed.
        Assert.Equal(0, await CountAsync(series, "seriesStatus", CatalogueFilterOperator.Includes, "Ended"));
    }

    /// <summary>
    /// A provider that does not answer must not blank what an earlier one did.
    /// TMDb returns a status for everything; smaller providers do not, and a
    /// refresh from one of those should not erase the network.
    /// </summary>
    [Fact]
    public async Task A_refresh_that_says_nothing_does_not_erase_what_is_known()
    {
        using var storage = TestStorage.Create();
        var series = await CreateAsync(storage);

        var id = await AddAsync(series, "Shogun");
        await RefreshAsync(series, id, status: "Ended", network: "FX");
        await RefreshAsync(series, id, status: null, network: null);

        Assert.Equal(1, await CountAsync(series, "seriesStatus", CatalogueFilterOperator.Includes, "Ended"));
        Assert.Equal(1, await CountAsync(series, "network", CatalogueFilterOperator.Is, "FX"));
    }

    /* ------------------------------------------------------------ helpers */

    private static Task<int> CountAsync(
        ISeriesCatalogRepository series,
        string field,
        CatalogueFilterOperator op,
        string value)
        => series
            .ListPageAsync(
                new CatalogueQuery(Filters: new CatalogueFilters([new CatalogueFilterCondition(field, op, [value])])),
                CancellationToken.None)
            .ContinueWith(page => page.Result.Items.Count, TaskScheduler.Default);

    private static Task RefreshAsync(
        ISeriesCatalogRepository series,
        string id,
        string? status,
        string? network)
        => series.UpdateMetadataAsync(
                new MediaMetadataUpdate(
                    id,
                    "tmdb",
                    "1', 'x",
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    "{}",
                    RuntimeMinutes: null,
                    Popularity: null,
                    VoteCount: null,
                    Status: status,
                    MadeBy: network),
                CancellationToken.None);

    private static async Task<string> AddAsync(ISeriesCatalogRepository series, string title)
    {
        await series.AddAsync(new CreateSeriesRequest(title, 2022, null), CancellationToken.None);
        return (await series.ListPageAsync(new CatalogueQuery(), CancellationToken.None))
            .Items.Single(item => item.Title == title).Id;
    }

    private static async Task<ISeriesCatalogRepository> CreateAsync(TestStorage storage)
    {
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-03-02T00:00:00Z"));

        await new SeriesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        return new SqliteSeriesCatalogRepository(storage.Factory, clock);
    }
}
