using Deluno.Libraries.Data;
using Deluno.Libraries.Routing;
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
        // #1033: picks the library a title routes to from Deluno's own
        // destination rules, evaluated on the title's metadata - shared by
        // every caller that used to guess or read a single fixed mapping.
        services.AddSingleton<ILibraryRoutingResolver, LibraryRoutingResolver>();
        // The narrow face of the same store. Registered separately so the two
        // catalogue pages can ask what a shelf wants without taking a
        // dependency on everything else a library knows.
        services.AddSingleton<ILibrarySubtitlePreferences>(
            provider => provider.GetRequiredService<ILibrariesRepository>());
        services.AddSingleton<ILibraryImportRunsRepository, SqliteLibraryImportRunsRepository>();
        services.AddSingleton<IPolicySetLibraryApplier, PolicySetLibraryApplier>();
        return services;
    }

    public static IEndpointRouteBuilder MapDelunoLibraries(this IEndpointRouteBuilder endpoints)
        => endpoints.MapDelunoLibrariesEndpoints();
}
