using Deluno.Libraries.Data;
using Deluno.Quality;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Libraries;

public static class LibrariesServiceCollectionExtensions
{
    public static IServiceCollection AddDelunoLibrariesModule(this IServiceCollection services)
    {
        services.AddSingleton<ILibrariesRepository, SqliteLibrariesRepository>();
        services.AddSingleton<ILibraryImportRunsRepository, SqliteLibraryImportRunsRepository>();
        services.AddSingleton<IPolicySetLibraryApplier, PolicySetLibraryApplier>();
        return services;
    }

    public static IEndpointRouteBuilder MapDelunoLibraries(this IEndpointRouteBuilder endpoints)
        => endpoints.MapDelunoLibrariesEndpoints();
}
