using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Movies.Migrations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Deluno.Movies.Data;

public sealed class MoviesSchemaInitializer(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    IDelunoDatabaseMigrator migrator,
    ILogger<MoviesSchemaInitializer> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await migrator.ApplyAsync(
            DelunoDatabaseNames.Movies,
            MoviesDatabaseMigrations.All,
            cancellationToken);

        logger.LogInformation(
            "Movies database migrations are current at {DatabasePath}.",
            databaseConnectionFactory.GetDatabasePath(DelunoDatabaseNames.Movies));
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}