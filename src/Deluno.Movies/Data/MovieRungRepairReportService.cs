using Deluno.Contracts;
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
    ILogger<MovieRungRepairReportService> logger,
    IBackgroundProblemReporter? problemReporter = null)
    : IHostedService, IBackgroundProblemRetry
{
    public const string ProblemKey = "startup:movie-rung-repair-report";

    string IBackgroundProblemRetry.ProblemKey => ProblemKey;

    public async Task StartAsync(CancellationToken cancellationToken) => await TryRunAsync(cancellationToken);

    /// <summary>The Health row's "Try again": runs the report now (#1080).</summary>
    public Task<bool> RetryAsync(CancellationToken cancellationToken) => TryRunAsync(cancellationToken);

    private async Task<bool> TryRunAsync(CancellationToken cancellationToken)
    {
        // A tidy-up report must never stop the host from starting (#1080); it runs again next start.
        try
        {
            await RunAsync(cancellationToken);
            problemReporter?.Clear(ProblemKey);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "This start-up tidy-up did not finish; it will run again on the next start.");
            // Same key each start, so a repeat failure updates one Health entry rather than adding another.
            problemReporter?.Report(
                ProblemKey,
                "A movie status repair was not reported",
                "Deluno could not write the Activity line about movies it put back on Quality met when it started. Nothing was searched for. Deluno tries again each time it starts.",
                exception.Message);
            return false;
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
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
