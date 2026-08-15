using Deluno.Jobs.Contracts;
using Deluno.Series.Data;
using Deluno.Series.Services;
using Deluno.Platform.Migration;
using Deluno.Series.Migration;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Series;

public static class SeriesServiceCollectionExtensions
{
    public static IServiceCollection AddDelunoSeriesModule(this IServiceCollection services)
    {
        services.AddSingleton<ISeriesCatalogRepository, SqliteSeriesCatalogRepository>();
        services.AddSingleton<ISeriesWorkflowService, SeriesWorkflowService>();
        services.AddSingleton<IEpisodeWorkflowService, EpisodeWorkflowService>();
        services.AddSingleton<IEpisodeImportRecoveryService, EpisodeImportRecoveryService>();
        services.AddSingleton<IDispatchRecoveryHandler, SeriesDispatchRecoveryHandler>();
        services.AddSingleton<IMigrationCatalogImporter, SeriesMigrationCatalogImporter>();
        services.AddHostedService<SeriesSchemaInitializer>();
        return services;
    }
}
