using Microsoft.Extensions.DependencyInjection;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Metadata;
using Deluno.Integrations.Search;

namespace Deluno.Integrations;

public static class IntegrationsServiceCollectionExtensions
{
    public static IServiceCollection AddDelunoIntegrationsModule(this IServiceCollection services)
    {
        services.AddSingleton<IReleaseRankingModelService, BoundedReleaseRankingModelService>();
        services.AddScoped<IMediaSearchPlanner, FeedMediaSearchPlanner>();
        services.AddScoped<IAcquisitionDecisionPipeline, AcquisitionDecisionPipeline>();
        services.AddHttpClient("indexers", client => client.Timeout = TimeSpan.FromSeconds(10));
        services.AddHttpClient("download-clients", client => client.Timeout = TimeSpan.FromSeconds(8));
        services.AddScoped<IDownloadClientTelemetryService, DownloadClientTelemetryService>();
        services.AddScoped<IDownloadClientGrabService, DownloadClientGrabService>();
        services.AddScoped<IDownloadClientWebhookService, DownloadClientWebhookService>();
        services.AddHttpClient<TmdbMetadataProvider>();
        services.AddScoped<IMetadataProvider>(sp => sp.GetRequiredService<TmdbMetadataProvider>());
        services.AddHostedService<CacheSchemaInitializer>();
        return services;
    }
}
