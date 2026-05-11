using Deluno.Jobs.Contracts;
using Deluno.Series.Data;
using Deluno.Series.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Series;

public static class SeriesServiceCollectionExtensions
{
    public static IServiceCollection AddDelunoSeriesModule(this IServiceCollection services)
    {
        services.AddSingleton<ISeriesCatalogRepository, SqliteSeriesCatalogRepository>();
        services.AddSingleton<ISeriesWorkflowService, SeriesWorkflowService>();
        services.AddSingleton<IDispatchRecoveryHandler, SeriesDispatchRecoveryHandler>();
        services.AddHostedService<SeriesSchemaInitializer>();
        return services;
    }
}
