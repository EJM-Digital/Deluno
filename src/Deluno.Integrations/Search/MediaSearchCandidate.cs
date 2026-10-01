using Deluno.Quality.ReleasePreferences;

namespace Deluno.Integrations.Search;

public sealed record MediaSearchCandidate(
    string ReleaseName,
    string IndexerId,
    string IndexerName,
    string Quality,
    int Score,
    bool MeetsCutoff,
    string Summary,
    string? DownloadUrl = null,
    long? SizeBytes = null,
    int? Seeders = null,
    string DecisionStatus = "eligible",
    IReadOnlyList<string>? DecisionReasons = null,
    IReadOnlyList<string>? RiskFlags = null,
    int QualityDelta = 0,
    int CustomFormatScore = 0,
    int SeederScore = 0,
    int SizeScore = 0,
    string? ReleaseGroup = null,
    double? EstimatedBitrateMbps = null,
    string PolicyVersion = Deluno.Quality.MediaPolicyCatalog.CurrentVersion,
    IReadOnlyList<CustomFormatMatchResult>? MatchedCustomFormats = null,
    PreferenceEvaluation? PreferenceEvaluation = null,
    PreferenceComparison? PreferenceComparison = null,
    // The torrent's info hash or the NZB's id/GUID, when the indexer's
    // listing already names one — a torznab infohash attribute, a magnet
    // link, or a newznab item's GUID (#802, #874). Null for a release only
    // offered as a .torrent link, since nothing names its hash before it is
    // fetched.
    string? TorrentHashOrItemId = null,
    // #753: enriched result columns, alongside the existing DecisionStatus
    // (a "rejected" status carries its own reason in DecisionReasons above).
    string? IndexerProtocol = null,
    double? ReleaseAgeHours = null,
    int? Leechers = null,
    // #969: how many times this release has already been grabbed. Blank
    // (null) when the indexer never reports it -- Torznab/Newznab's own
    // "grabs" attr, which the native tracker engine also writes.
    int? Grabs = null,
    // #1098: the first gate that refused this release, in the engine's own words, so the Live screen
    // can say why it was turned down. Null when nothing hard refused it.
    string? RejectionGate = null);
