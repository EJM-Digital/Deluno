using Deluno.Infrastructure.Storage;
using Deluno.Platform.Contracts;

namespace Deluno.Worker.Services;

/// <summary>Reads the address a cached artwork file was downloaded from (#1104), from the artwork cache.</summary>
public sealed class ArtworkRemoteUrls(IDelunoDatabaseConnectionFactory databaseConnectionFactory) : IArtworkRemoteUrls
{
    public async Task<string?> FindRemoteUrlAsync(string cacheKey, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Cache, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT remote_url FROM artwork_cache WHERE cache_key = @cacheKey LIMIT 1;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@cacheKey";
        parameter.Value = cacheKey;
        command.Parameters.Add(parameter);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }
}
