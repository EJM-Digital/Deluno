using Deluno.Downloader.Engine;
using Deluno.Downloader.Extraction;
using Deluno.Downloader.Nzb.Par2;
using Deluno.Downloader.Persistence;
using Deluno.Downloader.Postprocessing;
using Deluno.Downloader.Torrent.Engine;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Downloader.DependencyInjection;

/// <summary>
/// DI entry point for the built-in downloader engine.
///
/// Phase coverage as of this commit:
/// <list type="bullet">
///   <item><description>Phase 2 — shared: persistence (<see cref="DownloaderSchemaInitializer"/>, <c>SqliteJobRepository</c>),
///     extraction pipeline (SharpCompress + UnRAR wrapper), post-processing pipeline.</description></item>
///   <item><description>Phase 3a (NZB), 3b (Torrent), 4 (par2), 5 (integration adapters) — wired in subsequent phases.</description></item>
/// </list>
/// </summary>
public static class DownloaderServiceCollectionExtensions
{
    public static IServiceCollection AddDelunoBuiltInDownloaders(this IServiceCollection services)
    {
        // Persistence — schema migration + WAL tuning runs at startup.
        services.AddHostedService<DownloaderSchemaInitializer>();
        services.AddSingleton<IJobRepository, SqliteJobRepository>();
        services.AddSingleton<INzbServerRepository, SqliteNzbServerRepository>();

        // Extraction — register concrete extractors and the pipeline that
        // dispatches by detected format.
        services.AddSingleton<IArchiveExtractor, SharpCompressArchiveExtractor>();
        services.AddSingleton<IArchiveExtractor>(_ => new UnRarBinaryExtractor(
            // Resolves bundled tools/unrar/UnRAR.exe on Windows installs
            // (Velopack release pipeline drops it there per task #36).
            // Falls back to PATH on Linux/macOS where apt provides `unrar`.
            binaryPath: BundledBinaryResolver.Resolve(
                "unrar", OperatingSystem.IsWindows() ? "UnRAR.exe" : "unrar")));
        services.AddSingleton<ArchiveExtractionPipeline>();

        // par2 wrapper. Resolves bundled tools/par2/par2.exe on Windows
        // installs (Velopack release pipeline drops it there per task #36),
        // falls back to PATH on Linux/macOS where apt provides `par2`.
        services.AddSingleton<IPar2Service>(_ => new Par2BinaryService(
            BundledBinaryResolver.Resolve(
                "par2", OperatingSystem.IsWindows() ? "par2.exe" : "par2")));

        // Torrent engine. MonoTorrent wrapper; singleton because the
        // ClientEngine holds the listen socket + DHT node + active
        // TorrentManager set. Defaults bind v4+v6 on port 51413
        // (qBittorrent default — common firewall rules already know
        // about it).
        services.AddSingleton<ITorrentEngine>(_ => new MonoTorrentEngine());

        // Execution worker: polls the jobs table, dispatches queued jobs
        // to the right per-protocol executor, drives the lifecycle state
        // machine to PostProcessed (or Failed).
        services.AddHttpClient<NzbJobExecutor>();
        services.AddHttpClient<TorrentJobExecutor>();
        services.AddSingleton<IDownloaderJobExecutor>(sp => sp.GetRequiredService<NzbJobExecutor>());
        services.AddSingleton<IDownloaderJobExecutor>(sp => sp.GetRequiredService<TorrentJobExecutor>());
        services.AddHostedService<DownloaderJobExecutionService>();

        // Post-processing — default ordering: sample filter → flatten → sanitize.
        // Per-category overrides (e.g. skip flatten for torrents) live in
        // settings and the orchestrator picks the right pipeline per job.
        services.AddSingleton<SampleAndProofFilter>();
        services.AddSingleton<SubdirectoryFlattener>();
        services.AddSingleton<FileNameSanitizer>();
        services.AddSingleton(sp => new PostProcessingPipeline(
        [
            sp.GetRequiredService<SampleAndProofFilter>(),
            sp.GetRequiredService<SubdirectoryFlattener>(),
            sp.GetRequiredService<FileNameSanitizer>(),
        ]));

        return services;
    }
}
