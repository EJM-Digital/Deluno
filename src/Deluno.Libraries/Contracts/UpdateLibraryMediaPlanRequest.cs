namespace Deluno.Libraries.Contracts;

/// <summary>
/// Assigns the default Media Plan for a library. The plan remains the source of
/// truth for the library's quality profile and search timing until it is removed
/// or the library is explicitly given a profile override.
/// </summary>
public sealed record UpdateLibraryMediaPlanRequest(string? PolicySetId);
