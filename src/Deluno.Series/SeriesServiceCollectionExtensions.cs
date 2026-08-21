using Deluno.Contracts;
using Deluno.Jobs.Contracts;
using Deluno.Media;
using Deluno.Series.Data;
using Deluno.Series.Services;
using Deluno.Platform.Migration;
using Deluno.Series.Migration;
using Deluno.Recovery.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Deluno.Series;

public static class SeriesServiceCollectionExtensions
{
    public static IServiceCollection AddDelunoSeriesModule(this IServiceCollection services)
    {
        services.TryAddSingleton<IMediaStateRepository, SqliteMediaStateRepository>();
        services.AddSingleton<ISeriesCatalogRepository, SqliteSeriesCatalogRepository>();
        services.AddSingleton<ISeriesImportRecoveryRetentionRepository>(provider => provider.GetRequiredService<ISeriesCatalogRepository>());
        services.AddSingleton<ISeriesWorkflowService, SeriesWorkflowService>();
        services.AddSingleton<IEpisodeWorkflowService, EpisodeWorkflowService>();
        services.AddSingleton<IEpisodeImportRecoveryService, EpisodeImportRecoveryService>();
        services.AddSingleton<IDispatchRecoveryHandlerComponent, SeriesDispatchRecoveryHandler>();
        services.AddSingleton<IMigrationCatalogImporter, SeriesMigrationCatalogImporter>();
        services.AddHostedService<SeriesSchemaInitializer>();
        return services;
    }
}
