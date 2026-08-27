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
using Deluno.Quality;

namespace Deluno.Series;

public static class SeriesServiceCollectionExtensions
{
    public static IServiceCollection AddDelunoSeriesModule(this IServiceCollection services)
    {
        services.TryAddSingleton<IMediaStateRepository, SqliteMediaStateRepository>();
        services.TryAddSingleton<IMediaSubtitleRepository, SqliteMediaSubtitleRepository>();
        services.AddSingleton<ISeriesCatalogRepository, SqliteSeriesCatalogRepository>();
        // The quality ladder has to reach this catalogue's own database for a
        // shelf to be sortable by quality; the model service pushes it here on
        // save. Resolved from the repository so there is one connection, one
        // transaction and no second copy of the table names.
        services.AddSingleton<IQualityRankSink>(provider => provider.GetRequiredService<ISeriesCatalogRepository>());
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
