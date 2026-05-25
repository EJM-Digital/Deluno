using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Downloader.DependencyInjection;

/// <summary>
/// DI entry point for the built-in downloader engine.
///
/// Currently a stub — Phase 1 scaffolding only. Each subsequent phase
/// (per <c>docs/exec-plans/active/builtin-downloader-architecture.md</c>)
/// hangs more services off this method:
///
/// <list type="bullet">
///   <item><description>Phase 2 — shared layer: lifecycle state machine, persistence repositories, extraction, post-processing.</description></item>
///   <item><description>Phase 3a — NZB: NNTP connection pool, multi-server failover, yEnc decoder, NZB parser, orchestrator.</description></item>
///   <item><description>Phase 3b — Torrent: MonoTorrent wrapper, tracker manager, magnet ingestion.</description></item>
///   <item><description>Phase 4 — par2 binary integration.</description></item>
///   <item><description>Phase 5 — Integration adapters for protocol values "deluno-nzb" / "deluno-torrent".</description></item>
/// </list>
///
/// Callers register via:
/// <code>builder.Services.AddDelunoBuiltInDownloaders();</code>
/// </summary>
public static class DownloaderServiceCollectionExtensions
{
    public static IServiceCollection AddDelunoBuiltInDownloaders(this IServiceCollection services)
    {
        // Intentionally empty for Phase 1. Service registrations land in
        // subsequent phases; the method exists now so consumers (Host,
        // Worker) can wire up the call site without further plumbing
        // changes when Phase 2 services come online.
        return services;
    }
}
