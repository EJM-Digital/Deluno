namespace Deluno.Contracts.Manifest;

public static class DelunoSystemManifest
{
    public static IReadOnlyList<ModuleDescriptor> Modules { get; } =
    [
        new("Platform", "Accounts, settings, notifications, audit, and system health."),
        new("Movies", "Movie catalog, monitoring, import workflow, and history."),
        new("Series", "Series catalog, monitoring, import workflow, and history."),
        new("Jobs", "Durable job scheduling, leasing, attempts, and worker heartbeats."),
        new("Integrations", "Metadata, indexer, and download client abstractions."),
        new("Realtime", "SignalR hubs and live activity updates."),
        new("Filesystem", "Root policies, import targets, and path safety rules."),
        new("Downloader", "Built-in in-process NZB and BitTorrent engine, behind the same DownloadClients seam as remote SAB/qBit.")
    ];

    public static IReadOnlyList<DatabaseDescriptor> Databases { get; } =
    [
        new("platform", "platform.db", "Platform settings, credentials, notifications, and audit."),
        new("movies", "movies.db", "Movie catalog, monitoring state, and import records."),
        new("series", "series.db", "Shows, seasons, episodes, monitoring state, and import records."),
        new("jobs", "jobs.db", "Durable job schedules, leases, runs, attempts, and heartbeats."),
        new("cache", "cache.db", "Provider payload cache and transient normalization artifacts."),
        new("downloader", "downloader.db", "Built-in downloader jobs, files, history, NZB servers/segments, and torrent metadata/pieces/trackers.")
    ];
}
