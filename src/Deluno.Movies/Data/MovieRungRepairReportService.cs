using Deluno.Infrastructure.Storage;
using Deluno.Jobs.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Deluno.Movies.Data;

/// <summary>
/// Says once, in Activity, how many movies were put back on Quality met
/// by the rung repair (#1065). The migration records them; this runs after the
/// Movies schema initializer and marks them reported only after the line is
/// written, so an interrupted start reports again rather than never.
/// </summary>
public sealed class MovieRungRepairReportService(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    IActivityFeedRepository activityFeedRepository,
    ILogger<MovieRungRepairReportService> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Movies,
            cancellationToken);

        long count;
        using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT COUNT(DISTINCT movie_id) FROM movie_rung_repairs WHERE reported = 0;";
            count = Convert.ToInt64(await read.ExecuteScalarAsync(cancellationToken) ?? 0L);
        }

        if (count == 0)
        {
            return;
        }

        var one = count == 1;
        await activityFeedRepository.RecordActivityAsync(
            "library.movie.status-repaired",
            $"{count} {(one ? "movie" : "movies")} your quality rules told Deluno to leave alone {(one ? "was" : "were")} put back on Quality met. An earlier update had marked {(one ? "it" : "them")} Upgradable. Nothing was searched for.",
            null,
            null,
            null,
            null,
            cancellationToken);

        using var mark = connection.CreateCommand();
        mark.CommandText = "UPDATE movie_rung_repairs SET reported = 1 WHERE reported = 0;";
        await mark.ExecuteNonQueryAsync(cancellationToken);
        logger.LogInformation("Put {Count} movies back on Quality met after an earlier status repair.", count);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
