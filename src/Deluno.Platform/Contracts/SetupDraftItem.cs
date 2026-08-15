namespace Deluno.Platform.Contracts;

/// <summary>
/// A resumable guided-setup draft. It intentionally excludes API keys, passwords,
/// and metadata-provider secrets; users re-enter those when they choose to connect.
/// </summary>
public sealed record SetupDraftItem(
    string Mode = "simple",
    string MediaIntent = "both",
    string MovieRootPath = "",
    string SeriesRootPath = "",
    string DownloadsPath = "",
    string QualityPreset = "",
    string FormatGoal = "",
    string IndexerName = "",
    string IndexerProtocol = "torznab",
    string IndexerUrl = "",
    string ClientName = "",
    string ClientProtocol = "qbittorrent",
    string ClientHost = "",
    string ClientPort = "8080",
    string MetadataProviderMode = "broker",
    string MetadataBrokerUrl = "https://deluno-metadata-gateway.ejmdigital.workers.dev",
    bool BackupEnabled = true,
    string FirstTitleType = "movies",
    string FirstTitle = "",
    string FirstTitleYear = "",
    bool FirstTitleMonitored = true,
    DateTimeOffset UpdatedUtc = default);

public sealed record UpdateSetupDraftRequest(
    string Mode = "simple",
    string MediaIntent = "both",
    string MovieRootPath = "",
    string SeriesRootPath = "",
    string DownloadsPath = "",
    string QualityPreset = "",
    string FormatGoal = "",
    string IndexerName = "",
    string IndexerProtocol = "torznab",
    string IndexerUrl = "",
    string ClientName = "",
    string ClientProtocol = "qbittorrent",
    string ClientHost = "",
    string ClientPort = "8080",
    string MetadataProviderMode = "broker",
    string MetadataBrokerUrl = "https://deluno-metadata-gateway.ejmdigital.workers.dev",
    bool BackupEnabled = true,
    string FirstTitleType = "movies",
    string FirstTitle = "",
    string FirstTitleYear = "",
    bool FirstTitleMonitored = true);
