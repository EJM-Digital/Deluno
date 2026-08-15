using Deluno.Jobs.Contracts;
using Deluno.Movies.Data;
using Deluno.Movies.Services;
using Deluno.Platform.Quality;
using Deluno.Platform.Migration;
using Deluno.Movies.Migration;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Movies;

public static class MoviesServiceCollectionExtensions
{
    public static IServiceCollection AddDelunoMoviesModule(this IServiceCollection services)
    {
        services.AddSingleton<IMovieCatalogRepository, SqliteMovieCatalogRepository>();
        services.AddSingleton<IMovieWorkflowService, MovieWorkflowService>();
        services.AddSingleton<IDispatchRecoveryHandler, MovieDispatchRecoveryHandler>();
        services.AddSingleton<IMigrationCatalogImporter, MovieMigrationCatalogImporter>();
        services.AddHostedService<MoviesSchemaInitializer>();
        return services;
    }
}
