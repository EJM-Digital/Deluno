using Deluno.Infrastructure.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Deluno.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddDelunoInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<StoragePathOptions>()
            .Bind(configuration.GetSection(StoragePathOptions.SectionName))
            .PostConfigure<IHostEnvironment>((options, environment) =>
            {
                var configuredRoot = string.IsNullOrWhiteSpace(options.DataRoot)
                    ? "data"
                    : options.DataRoot;

                options.DataRoot = Path.GetFullPath(configuredRoot, environment.ContentRootPath);
            });

        services.AddSingleton<IDelunoDatabaseConnectionFactory, SqliteDatabaseConnectionFactory>();
        services.AddHostedService<DelunoStorageBootstrapService>();
        services.AddSingleton(TimeProvider.System);
        return services;
    }
}
