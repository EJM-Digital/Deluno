using Deluno.Contracts;
using Deluno.Jobs.Contracts;
using Deluno.Media;
using Deluno.Movies.Data;
using Deluno.Movies.Services;
using Deluno.Quality;
using Deluno.Platform.Migration;
using Deluno.Movies.Migration;
using Deluno.Recovery.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Deluno.Movies;

public static class MoviesServiceCollectionExtensions
{
    public static IServiceCollection AddDelunoMoviesModule(this IServiceCollection services)
    {
        services.TryAddSingleton<IMediaStateRepository, SqliteMediaStateRepository>();
        services.AddSingleton<IMovieCatalogRepository, SqliteMovieCatalogRepository>();
        services.AddSingleton<IMovieImportRecoveryRetentionRepository>(provider => provider.GetRequiredService<IMovieCatalogRepository>());
        services.AddSingleton<IMovieWorkflowService, MovieWorkflowService>();
        services.AddSingleton<IDispatchRecoveryHandlerComponent, MovieDispatchRecoveryHandler>();
        services.AddSingleton<IMigrationCatalogImporter, MovieMigrationCatalogImporter>();
        services.AddHostedService<MoviesSchemaInitializer>();
        return services;
    }
}
