using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Catalogue;

/// <summary>
/// A show is protected from downgrades from the moment it is added, and there
/// is no switch to turn that off for a show (the film page keeps its own). The
/// route that used to flip it for a show is gone, so the default is the only
/// state a show can be in; this is the test that says so.
/// </summary>
public sealed class ShowsAreProtectedFromDowngradesByDefaultTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-03T00:00:00Z");

    [Fact]
    public async Task A_show_that_has_just_been_given_a_library_is_protected_from_downgrades()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Now);
        await new SeriesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var series = new SqliteSeriesCatalogRepository(storage.Factory, clock);

        await series.AddAsync(new CreateSeriesRequest("Severance", 2022, null), CancellationToken.None);
        var show = Assert.Single(await series.ListAsync(CancellationToken.None));

        await series.EnsureWantedStateAsync(
            show.Id, "tv-library", "missing", "Deluno is looking for this show.",
            hasFile: false, currentQuality: null, targetQuality: "WEB 1080p", qualityCutoffMet: false,
            CancellationToken.None);

        var wanted = await series.GetSeriesWantedStateAsync(show.Id, "tv-library", CancellationToken.None);

        Assert.NotNull(wanted);
        Assert.True(wanted.PreventLowerQualityReplacements);
    }
}
