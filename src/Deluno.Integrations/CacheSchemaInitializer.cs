using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Integrations.Migrations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Deluno.Integrations;

public sealed class CacheSchemaInitializer(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    IDelunoDatabaseMigrator migrator,
    ILogger<CacheSchemaInitializer> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await migrator.ApplyAsync(
            DelunoDatabaseNames.Cache,
            CacheDatabaseMigrations.All,
            cancellationToken);

        logger.LogInformation(
            "Cache database migrations are current at {DatabasePath}.",
            databaseConnectionFactory.GetDatabasePath(DelunoDatabaseNames.Cache));
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}