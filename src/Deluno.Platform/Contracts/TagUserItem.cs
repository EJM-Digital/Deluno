namespace Deluno.Platform.Contracts;

/// <summary>
/// One connection or rule that names a tag (#1039). <see cref="Kind"/> is a
/// stable key the screen maps to plain words; <see cref="Link"/> is the page
/// where the user changes it.
/// </summary>
public sealed record TagUserItem(
    string Kind,
    string Id,
    string Name,
    string Link);
