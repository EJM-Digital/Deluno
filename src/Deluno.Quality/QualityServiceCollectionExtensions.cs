using Deluno.Quality.Data;
using Deluno.Quality.Guides;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Quality;

public static class QualityServiceCollectionExtensions
{
    public static IServiceCollection AddDelunoQualityModule(this IServiceCollection services)
    {
        services.AddSingleton<IQualityRepository, SqliteQualityRepository>();
        services.AddSingleton<IVersionedMediaPolicyEngine, VersionedMediaPolicyEngine>();
        services.AddSingleton<IQualityModelService, QualityModelService>();
        services.AddSingleton<IMediaDecisionService, MediaDecisionService>();
        services.AddSingleton<IReleasePreferencePlanRepository, SqliteReleasePreferencePlanRepository>();
        services.AddSingleton<IPlaybackGoalRepository, SqlitePlaybackGoalRepository>();
        services.AddSingleton<IGuidePackageStore, SqliteGuidePackageStore>();
        services.AddHttpClient(GuideUpstreamTreeClient.HttpClientName, client =>
        {
            client.BaseAddress = new Uri("https://api.github.com/");
            client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Deluno guide-update-check/1.0");
        });
        services.AddSingleton<GuideUpstreamTreeClient>();
        services.AddSingleton<IGuideUpdateCheckStore, SqliteGuideUpdateCheckStore>();
        services.AddSingleton<IGuideUpdateCheckService, GuideUpdateCheckService>();
        services.AddHostedService<GuideUpdateCheckHostedService>();
        return services;
    }

    public static IEndpointRouteBuilder MapDelunoQuality(this IEndpointRouteBuilder endpoints)
        => endpoints.MapDelunoQualityEndpoints();
}
