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

        // Extraction — register concrete extractors and the pipeline that
        // dispatches by detected format.
        services.AddSingleton<IArchiveExtractor, SharpCompressArchiveExtractor>();
        services.AddSingleton<IArchiveExtractor>(_ => new UnRarBinaryExtractor(
            // Resolves via PATH; Phase 4 will bundle UnRAR.exe / unrar
            // under tools/unrar/ and pass an absolute path here.
            binaryPath: OperatingSystem.IsWindows() ? "UnRAR.exe" : "unrar"));
        services.AddSingleton<ArchiveExtractionPipeline>();

        // par2 wrapper. Binary path defaults to PATH lookup (`par2`).
        // Phase 4 release work bundles par2cmdline-turbo per-platform
        // under tools/par2/<rid>/ and passes an absolute path here.
        services.AddSingleton<IPar2Service>(_ => new Par2BinaryService("par2"));

        // Torrent engine. MonoTorrent wrapper; singleton because the
        // ClientEngine holds the listen socket + DHT node + active
        // TorrentManager set. Defaults bind v4+v6 on port 51413
        // (qBittorrent default — common firewall rules already know
        // about it).
        services.AddSingleton<ITorrentEngine>(_ => new MonoTorrentEngine());

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
