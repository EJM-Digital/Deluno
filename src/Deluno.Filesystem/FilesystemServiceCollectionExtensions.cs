using Deluno.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Filesystem;

public static class FilesystemServiceCollectionExtensions
{
    public static IServiceCollection AddDelunoFilesystemModule(this IServiceCollection services)
    {
        services.AddSingleton<IExistingLibraryImportService, ExistingLibraryImportService>();
        services.AddScoped<IImportPipelineService, ImportPipelineService>();
        return services;
    }
}
