using Deluno.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Filesystem;

public static class FilesystemServiceCollectionExtensions
{
    public static IServiceCollection AddDelunoFilesystemModule(this IServiceCollection services)
    {
        services.AddSingleton<IExistingLibraryImportService, ExistingLibraryImportService>();
        services.AddSingleton<IMediaProbeService, FfprobeMediaProbeService>();
        services.AddSingleton<ISubtitleInventoryService, SubtitleInventoryService>();
        // DESIGN-002 rule 5: the code that owns files writes them.
        services.AddSingleton<ISubtitleFileWriter, SubtitleFileWriter>();
        services.AddScoped<IImportPipelineService, ImportPipelineService>();
        services.AddScoped<IFilesystemReconciliationService, FilesystemReconciliationService>();
        return services;
    }
}
