using Deluno.Contracts.Manifest;
using Deluno.Api.Backup;
using Deluno.Api.Health;
using Deluno.Infrastructure.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Deluno.Api;

public static class DelunoApiExtensions
{
    public static IServiceCollection AddDelunoApi(this IServiceCollection services)
    {
        services.AddRouting();
        services.AddSingleton<DelunoBackupService>();
        services.AddSingleton<IDelunoBackupService>(sp => sp.GetRequiredService<DelunoBackupService>());
        services.AddHostedService(sp => sp.GetRequiredService<DelunoBackupService>());
        services.AddSingleton<IDelunoReadinessService, DelunoReadinessService>();
        return services;
    }

    public static IEndpointRouteBuilder MapDelunoApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/health", () => Results.Ok(DelunoReadinessService.Live()));

        var api = endpoints.MapGroup("/api");

        api.MapGet("/", () => Results.Ok(new
        {
            name = "Deluno",
            kind = "media-automation",
            architecture = "sqlite-first modular monolith"
        }));

        api.MapGet("/health/live", () => Results.Ok(DelunoReadinessService.Live()));

        api.MapGet("/health/ready", async (
            IDelunoReadinessService readiness,
            CancellationToken cancellationToken) =>
        {
            var result = await readiness.CheckAsync(cancellationToken);
            return Results.Json(
                result,
                statusCode: result.Ready
                    ? StatusCodes.Status200OK
                    : StatusCodes.Status503ServiceUnavailable);
        });

        api.MapGet("/manifest", (IOptions<StoragePathOptions> storage) => Results.Ok(new
        {
            app = "Deluno",
            storageRoot = storage.Value.DataRoot,
            modules = DelunoSystemManifest.Modules,
            databases = DelunoStorageLayout.Databases
        }));

        return endpoints;
    }
}
