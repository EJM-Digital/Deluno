using Deluno.Quality.Data;
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
        return services;
    }

    public static IEndpointRouteBuilder MapDelunoQuality(this IEndpointRouteBuilder endpoints)
        => endpoints.MapDelunoQualityEndpoints();
}
