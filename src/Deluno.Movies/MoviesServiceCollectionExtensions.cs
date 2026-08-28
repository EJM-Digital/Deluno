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

        // Registered beside the state it repairs. It is the other half of
        // WantedStatuses.Downloading: without it a failed dispatch leaves a
        // title claiming to download for ever, and never searched again.
        services.TryAddSingleton<ILiveDownloadLookup, DispatchLiveDownloadLookup>();
        services.TryAddSingleton<IDownloadStateReconciler, DownloadStateReconciler>();
        services.TryAddSingleton<IMediaSubtitleRepository, SqliteMediaSubtitleRepository>();
        services.AddSingleton<IMovieCatalogRepository, SqliteMovieCatalogRepository>();
        // The quality ladder has to reach this catalogue's own database for a
        // shelf to be sortable by quality; the model service pushes it here on
        // save. Resolved from the repository so there is one connection, one
        // transaction and no second copy of the table names.
        services.AddSingleton<IQualityRankSink>(provider => provider.GetRequiredService<IMovieCatalogRepository>());
        services.AddSingleton<IMovieImportRecoveryRetentionRepository>(provider => provider.GetRequiredService<IMovieCatalogRepository>());
        services.AddSingleton<IMovieWorkflowService, MovieWorkflowService>();
        services.AddSingleton<IDispatchRecoveryHandlerComponent, MovieDispatchRecoveryHandler>();
        services.AddSingleton<IMigrationCatalogImporter, MovieMigrationCatalogImporter>();
        services.AddHostedService<MoviesSchemaInitializer>();
        return services;
    }
}
