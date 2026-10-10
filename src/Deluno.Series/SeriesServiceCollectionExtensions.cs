using Deluno.Contracts;
using Deluno.Jobs.Contracts;
using Deluno.Integrations.Metadata;
using Deluno.Media;
using Deluno.Platform;
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
        services.TryAddSingleton<IMediaSubtitleRepository, SqliteMediaSubtitleRepository>();
        services.TryAddSingleton<IMediaSubtitleOverrideRepository, SqliteMediaSubtitleOverrideRepository>();
        services.AddSingleton<ISeriesCatalogRepository, SqliteSeriesCatalogRepository>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<Deluno.Platform.Contracts.IHandoffClosedListener, SeriesHandoffClosedListener>());
        // The quality ladder has to reach this catalogue's own database for a
        // shelf to be sortable by quality; the model service pushes it here on
        // save. Resolved from the repository so there is one connection, one
        // transaction and no second copy of the table names.
        services.AddSingleton<IQualityRankSink>(provider => provider.GetRequiredService<ISeriesCatalogRepository>());
        services.AddSingleton<ISeriesImportRecoveryRetentionRepository>(provider => provider.GetRequiredService<ISeriesCatalogRepository>());
        // Scoped, not singleton: it consumes IAcquisitionDecisionPipeline and
        // IDownloadClientGrabService, which are both scoped.
        services.TryAddScoped<ISeasonSearchExecutor, SeasonSearchExecutor>();
        services.AddSingleton<IEpisodeWorkflowService, EpisodeWorkflowService>();
        services.AddSingleton<IEpisodeImportRecoveryService, EpisodeImportRecoveryService>();
        services.AddSingleton<IDispatchRecoveryHandlerComponent, SeriesDispatchRecoveryHandler>();
        services.AddSingleton<IDispatchTitleUnmonitorComponent, SeriesDispatchTitleUnmonitorService>();
        services.AddSingleton<IMigrationCatalogImporter, SeriesMigrationCatalogImporter>();
        services.AddSingleton<ISceneMappingRefreshService, SceneMappingRefreshService>();
        // Scoped, not singleton: it consumes IMetadataProvider, which is
        // scoped. The hosted service that schedules it creates its own scope
        // per pass, the same way HealthCheckHostedService does.
        services.TryAddScoped<IEpisodeGroupRefreshService, EpisodeGroupRefreshService>();
        services.AddHostedService<SeriesSchemaInitializer>();
        services.AddHostedService<SceneMappingRefreshHostedService>();
        services.AddHostedService<EpisodeGroupRefreshHostedService>();
        return services;
    }
}
