using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Series.Migrations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Deluno.Series.Data;

public sealed class SeriesSchemaInitializer(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    IDelunoDatabaseMigrator migrator,
    ILogger<SeriesSchemaInitializer> logger,
    IDelunoStartupGate? startupGate = null)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await migrator.ApplyAsync(
                DelunoDatabaseNames.Series,
                SeriesDatabaseMigrations.All,
                cancellationToken);
            startupGate?.MarkReady(DelunoDatabaseNames.Series);
        }
        catch (Exception exception)
        {
            startupGate?.MarkFailed(DelunoDatabaseNames.Series, exception);
            throw;
        }

        logger.LogInformation(
            "Series database migrations are current at {DatabasePath}.",
            databaseConnectionFactory.GetDatabasePath(DelunoDatabaseNames.Series));
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
