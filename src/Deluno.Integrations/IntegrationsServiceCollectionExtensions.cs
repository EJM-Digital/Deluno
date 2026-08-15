using Microsoft.Extensions.DependencyInjection;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Metadata;
using Deluno.Integrations.Processors;
using Deluno.Integrations.Search;
using Deluno.Platform.Contracts;

namespace Deluno.Integrations;

public static class IntegrationsServiceCollectionExtensions
{
    public static IServiceCollection AddDelunoIntegrationsModule(this IServiceCollection services)
    {
        services.AddSingleton<IReleaseRankingTrainingDataSource, SqliteReleaseRankingTrainingDataSource>();
        services.AddSingleton<MlNetReleaseRankingModelService>();
        services.AddSingleton<IReleaseRankingModelService>(provider => provider.GetRequiredService<MlNetReleaseRankingModelService>());
        services.AddSingleton<IReleaseRankingModelAdminService>(provider => provider.GetRequiredService<MlNetReleaseRankingModelService>());
        services.AddHostedService<RankingModelTrainingHostedService>();
        services.AddSingleton<IIntelligentRoutingService, IntelligentRoutingService>();
        services.AddScoped<IMediaSearchPlanner, FeedMediaSearchPlanner>();
        services.AddScoped<IAcquisitionDecisionPipeline, AcquisitionDecisionPipeline>();
        services.AddHttpClient("indexers", client => client.Timeout = TimeSpan.FromSeconds(10));
        services.AddHttpClient("download-clients", client => client.Timeout = TimeSpan.FromSeconds(8));
        services.AddHttpClient("processor-connections", client => client.Timeout = TimeSpan.FromSeconds(10));
        services.AddScoped<IDownloadClientTelemetryService, DownloadClientTelemetryService>();
        services.AddScoped<IDownloadClientGrabService, DownloadClientGrabService>();
        services.AddScoped<IDownloadClientWebhookService, DownloadClientWebhookService>();
        services.AddScoped<IProcessorConnectionService, ProcessorConnectionService>();

        services.AddHttpClient<TmdbMetadataProvider>();
        services.AddScoped<IMetadataProvider>(sp => sp.GetRequiredService<TmdbMetadataProvider>());
        services.AddHostedService<CacheSchemaInitializer>();
        return services;
    }
}
