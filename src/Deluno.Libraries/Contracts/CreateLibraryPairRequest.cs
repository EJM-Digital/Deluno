namespace Deluno.Libraries.Contracts;

/// <summary>
/// #1411: "Both" when adding a library. One name and one root become two linked libraries, "{Name} Movies" at
/// "{RootPath}/Movies" and "{Name} TV" at "{RootPath}/TV", each an ordinary library of its own engine.
/// </summary>
public sealed record CreateLibraryPairRequest(
    string? Name,
    string? RootPath,
    string? DownloadsPath = null,
    string? MoviesQualityProfileId = null,
    string? TvQualityProfileId = null,
    string? Purpose = null,
    bool AutoSearchEnabled = true,
    bool MissingSearchEnabled = true,
    bool UpgradeSearchEnabled = true);
