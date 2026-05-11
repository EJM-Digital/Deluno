using Deluno.Jobs.Contracts;
using Deluno.Movies.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Movies;

public static class MoviesServiceCollectionExtensions
{
    public static IServiceCollection AddDelunoMoviesModule(this IServiceCollection services)
    {
        services.AddSingleton<IMovieCatalogRepository, SqliteMovieCatalogRepository>();
        services.AddSingleton<IDispatchRecoveryHandler, MovieDispatchRecoveryHandler>();
        services.AddHostedService<MoviesSchemaInitializer>();
        return services;
    }
}
