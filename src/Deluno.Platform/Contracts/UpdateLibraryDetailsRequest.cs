namespace Deluno.Platform.Contracts;

/// <summary>
/// Rename a library or move its folders. Media type is fixed after creation;
/// automation, quality profile, media plan, workflow and routing have their own endpoints.
/// </summary>
public sealed record UpdateLibraryDetailsRequest(
    string? Name,
    string? RootPath,
    string? DownloadsPath);
