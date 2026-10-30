using Deluno.Infrastructure.Storage;
using Deluno.Media;
using Deluno.Persistence.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1435: a show whose episodes are all unmonitored sits on the Covered rung because nothing is wanted, and its stored reason must
/// say so in a plain sentence, never "All 0 aired episodes are here". A show with a monitored aired episode on disk still reads
/// "All N aired episodes are here". Both run through the real series database and the real rung writer.
/// </summary>
public sealed class AShowWithNoMonitoredEpisodeSaysNothingIsWantedTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private static async Task<string?> ReasonAfterRefreshAsync(ApplicationTestHost app, ApiScenes.Show show, int monitored)
    {
        await ApiScenes.ExecuteAsync(app, "series", $"UPDATE episode_entries SET monitored = {monitored}, air_date_utc = '2020-01-01T00:00:00.0000000+00:00' WHERE series_id = '{show.SeriesId}';");
        var factory = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        await using (var connection = (SqliteConnection)await factory.OpenConnectionAsync("series", CancellationToken.None))
        {
            await SeriesRungWriter.RefreshAsync(connection, null, show.SeriesId, show.LibraryId, Now, CancellationToken.None);
        }

        return await ApiScenes.TextAsync(app, "series", $"SELECT wanted_status || '|' || wanted_reason FROM series_wanted_state WHERE series_id = '{show.SeriesId}';");
    }

    [Fact]
    public async Task AShowWithNoMonitoredEpisodeSaysNoneAreMonitoredSoNothingIsWanted()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var show = await ApiScenes.ShowOnDiskAsync(app);

        var stored = await ReasonAfterRefreshAsync(app, show, monitored: 0);

        Assert.Equal("covered|None of this show's episodes are monitored, so nothing is wanted.", stored);
    }

    [Fact]
    public async Task AShowWithItsMonitoredAiredEpisodesAllHereStillSaysSo()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var show = await ApiScenes.ShowOnDiskAsync(app);

        var stored = await ReasonAfterRefreshAsync(app, show, monitored: 1);

        Assert.Equal("covered|The 1 aired episode is here at the quality you asked for.", stored);
    }
}
