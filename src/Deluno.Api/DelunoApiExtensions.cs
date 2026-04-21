using Deluno.Contracts.Manifest;
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
        return services;
    }

    public static IEndpointRouteBuilder MapDelunoApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api");

        api.MapGet("/", () => Results.Ok(new
        {
            name = "Deluno",
            kind = "media-automation",
            architecture = "sqlite-first modular monolith"
        }));

        api.MapGet("/health", () => Results.Ok(new
        {
            ok = true,
            timestamp = DateTimeOffset.UtcNow
        }));

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
