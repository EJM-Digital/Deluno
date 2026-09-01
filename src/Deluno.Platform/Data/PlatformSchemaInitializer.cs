using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Platform.Migrations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Deluno.Platform.Data;

public sealed class PlatformSchemaInitializer(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    IDelunoDatabaseMigrator migrator,
    ILogger<PlatformSchemaInitializer> logger,
    IDelunoStartupGate? startupGate = null)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await migrator.ApplyAsync(
                DelunoDatabaseNames.Platform,
                PlatformDatabaseMigrations.All,
                cancellationToken);
            startupGate?.MarkReady(DelunoDatabaseNames.Platform);
        }
        catch (Exception exception)
        {
            startupGate?.MarkFailed(DelunoDatabaseNames.Platform, exception);
            throw;
        }

        logger.LogInformation(
            "Platform database migrations are current at {DatabasePath}.",
            databaseConnectionFactory.GetDatabasePath(DelunoDatabaseNames.Platform));
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
