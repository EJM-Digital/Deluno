namespace Deluno.Libraries.Contracts;

/// <summary>
/// Which quality profile judges a library's titles (#1169). The profile must be
/// one for the library's own kind of media, because a film profile has no say
/// over episodes.
/// </summary>
public sealed record UpdateLibraryQualityProfileRequest(string? QualityProfileId);
