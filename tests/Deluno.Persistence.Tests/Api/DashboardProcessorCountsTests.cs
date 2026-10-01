using Deluno.Infrastructure.Storage;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Worker.Dashboard;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The Dashboard's processor counts read the hand-off statuses, not the
/// processor's own job states (#1099): the waiting count was always 0.
/// </summary>
public sealed class DashboardProcessorCountsTests
{
    [Fact]
    public async Task Waiting_and_went_wrong_count_the_hand_off_statuses_that_mean_them()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var databases = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        var now = DateTimeOffset.UtcNow.ToString("O");

        var rows = ProcessorHandoffStatuses.All.Select((status, index) =>
            $"('h{index}', 'lib', 'movies', 'c', 'q{index}', 'R{index}', '/r{index}', 'k{index}', 'Weir', NULL, '{status}', '{now}', '{now}')");
        await using (var connection = await databases.OpenConnectionAsync(DelunoDatabaseNames.Platform))
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO processor_handoffs (id, library_id, media_type, client_id, queue_item_id, release_name, source_path, source_key, processor_name, processor_connection_id, status, created_utc, updated_utc) VALUES "
                + string.Join(", ", rows) + ";";
            await command.ExecuteNonQueryAsync();
        }

        var counts = await app.Services.GetRequiredService<DashboardCountsStore>()
            .GetProcessorCountsAsync(DateTimeOffset.UtcNow.Date, CancellationToken.None);

        // waiting, submitted, accepted, started
        Assert.Equal(4, counts.Waiting);
        // failed, rejected, timed-out, stalled, lost
        Assert.Equal(5, counts.FailedHandoffs);
        Assert.Equal(ProcessorHandoffStatuses.All.Count, counts.HandoffsToday);
    }
}
