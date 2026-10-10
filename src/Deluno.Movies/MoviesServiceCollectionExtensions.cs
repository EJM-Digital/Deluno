using Deluno.Contracts;
using Deluno.Jobs.Contracts;
using Deluno.Integrations.Metadata;
using Deluno.Media;
using Deluno.Platform;
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
        // Re-decides what is owned when a profile, a library's profile or a title's own profile changes (#1169).
        services.TryAddSingleton<IWantedStateReevaluator, WantedStateReevaluator>();
        services.TryAddSingleton<IProfileChangeReevaluator>(provider => provider.GetRequiredService<IWantedStateReevaluator>());
        // What lets the Add screen say "you already have this". Registered
        // beside the repository it asks, and by both media modules, so the
        // answer exists wherever a catalogue does.
        services.TryAddSingleton<IMetadataLibraryPresence, MediaStateLibraryPresence>();
        services.TryAddSingleton<IMediaTagStore, SqliteMediaTagStore>();
        // #1024: one auto-tagging service for every add path, edit and refresh.
        services.TryAddSingleton<IAutoTaggingService, AutoTaggingService>();
        // Answers "why will this not download". Registered by both media
        // modules, like the state repository it reads.
        services.TryAddSingleton<AcquisitionBlockerGatherer>();
        // Scoped, not singleton: it consumes IDownloadClientTelemetryService,
        // which is scoped. Registering it as a singleton captures that
        // dependency, and the composition test refuses to build the container.
        services.TryAddScoped<AcquisitionOverrideService>();
        services.TryAddScoped<TitleRemovalService>();
        // #1161: keeps a removed title with its files in the recycle bin and puts it back.
        services.TryAddScoped<TitleSnapshotService>();
        services.TryAddScoped<ITitleSnapshotService>(provider => provider.GetRequiredService<TitleSnapshotService>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ITitleRestorer, TitleSnapshotService>(provider => provider.GetRequiredService<TitleSnapshotService>()));

        // Registered beside the state it repairs. It is the other half of
        // WantedStatuses.Downloading: without it a failed dispatch leaves a
        // title claiming to download for ever, and never searched again.
        services.TryAddSingleton<ILiveDownloadLookup, DispatchLiveDownloadLookup>();
        services.TryAddSingleton<IDownloadStateReconciler, DownloadStateReconciler>();
        services.TryAddSingleton<IMediaSubtitleRepository, SqliteMediaSubtitleRepository>();
        services.TryAddSingleton<IMediaSubtitleOverrideRepository, SqliteMediaSubtitleOverrideRepository>();
        services.AddSingleton<IMovieCatalogRepository, SqliteMovieCatalogRepository>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<Deluno.Platform.Contracts.IHandoffClosedListener, MovieHandoffClosedListener>());
        services.AddSingleton<IMovieCollectionsRepository, SqliteMovieCollectionsRepository>();
        // The quality ladder has to reach this catalogue's own database for a
        // shelf to be sortable by quality; the model service pushes it here on
        // save. Resolved from the repository so there is one connection, one
        // transaction and no second copy of the table names.
        services.AddSingleton<IQualityRankSink>(provider => provider.GetRequiredService<IMovieCatalogRepository>());
        services.AddSingleton<IMovieImportRecoveryRetentionRepository>(provider => provider.GetRequiredService<IMovieCatalogRepository>());
        services.AddSingleton<IMovieWorkflowService, MovieWorkflowService>();
        services.AddScoped<IMovieCollectionService, MovieCollectionService>();
        services.AddSingleton<IDispatchRecoveryHandlerComponent, MovieDispatchRecoveryHandler>();
        services.AddSingleton<IDispatchTitleUnmonitorComponent, MovieDispatchTitleUnmonitorService>();
        services.AddSingleton<IMigrationCatalogImporter, MovieMigrationCatalogImporter>();
        services.AddHostedService<MoviesSchemaInitializer>();
        services.AddSingleton<CollectionExclusionMigrationHostedService>();
        services.AddHostedService(provider => provider.GetRequiredService<CollectionExclusionMigrationHostedService>());
        services.AddSingleton<IBackgroundProblemRetry>(provider => provider.GetRequiredService<CollectionExclusionMigrationHostedService>());
        services.AddSingleton<MovieRungRepairReportService>();
        services.AddHostedService(provider => provider.GetRequiredService<MovieRungRepairReportService>());
        services.AddSingleton<IBackgroundProblemRetry>(provider => provider.GetRequiredService<MovieRungRepairReportService>());
        return services;
    }
}
