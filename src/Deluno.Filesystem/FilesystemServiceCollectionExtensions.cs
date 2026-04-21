using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Filesystem;

public static class FilesystemServiceCollectionExtensions
{
    public static IServiceCollection AddDelunoFilesystemModule(this IServiceCollection services)
    {
        return services;
    }
}

