using Deluno.Series.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Series;

public static class SeriesServiceCollectionExtensions
{
    public static IServiceCollection AddDelunoSeriesModule(this IServiceCollection services)
    {
        services.AddSingleton<ISeriesCatalogRepository, SqliteSeriesCatalogRepository>();
        services.AddHostedService<SeriesSchemaInitializer>();
        return services;
    }
}
