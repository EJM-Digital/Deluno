using Deluno.Contracts;
using Deluno.Filesystem.Subtitles;
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

        // Timing sync. Registered beside the writer because it is the same
        // bargain: Integrations decides a subtitle needs it, Filesystem is the
        // only module allowed to open the video and rewrite the file.
        services.AddSingleton<ISpeechDetector, FfmpegSpeechDetector>();
        services.AddSingleton<ISubtitleTimingSync, SubtitleTimingSyncService>();
        services.AddScoped<IImportPipelineService, ImportPipelineService>();
        services.AddScoped<IFilesystemReconciliationService, FilesystemReconciliationService>();
        return services;
    }
}
