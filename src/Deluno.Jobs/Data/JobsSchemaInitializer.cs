using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Jobs.Migrations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Deluno.Jobs.Data;

public sealed class JobsSchemaInitializer(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    IDelunoDatabaseMigrator migrator,
    ILogger<JobsSchemaInitializer> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await migrator.ApplyAsync(
            DelunoDatabaseNames.Jobs,
            JobsDatabaseMigrations.All,
            cancellationToken);

        logger.LogInformation(
            "Jobs database migrations are current at {DatabasePath}.",
            databaseConnectionFactory.GetDatabasePath(DelunoDatabaseNames.Jobs));
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}