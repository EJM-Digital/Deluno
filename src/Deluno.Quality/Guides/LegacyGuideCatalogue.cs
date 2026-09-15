using System.Text.Json;
using Deluno.Quality.ReleasePreferences;

namespace Deluno.Quality.Guides;

/// <summary>
/// The guide catalogue Deluno shipped before genuine TRaSH formats (package
/// version 3). Its ids were not TRaSH's: some were invented and some belonged
/// to unrelated TRaSH formats - "Blu-ray" carried the id of TRaSH's "Upscaled".
/// Rules people made from it still carry those ids and Deluno's patterns, so
/// this records, for each old entry, what it becomes for movies and for TV: a
/// genuine TRaSH format, a Deluno-owned extra, or nothing.
/// </summary>
public sealed record LegacyGuideCatalogue(
    string Description,
    int PackageVersion,
    IReadOnlyList<LegacyGuideCatalogueEntry> Entries)
{
    private const string ResourceName = "Deluno.Quality.Guides.trash-guide-legacy-catalogue.json";

    public static LegacyGuideCatalogue Current { get; } = Load();

    public LegacyGuideCatalogueEntry? Find(string? trashId)
        => string.IsNullOrWhiteSpace(trashId)
            ? null
            : Entries.FirstOrDefault(entry => string.Equals(entry.LegacyTrashId, trashId.Trim(), StringComparison.OrdinalIgnoreCase));

    private static LegacyGuideCatalogue Load()
    {
        using var stream = typeof(LegacyGuideCatalogue).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded legacy guide catalogue '{ResourceName}' was not found.");
        return JsonSerializer.Deserialize<LegacyGuideCatalogue>(stream, ReleasePreferenceJson.Options)
            ?? throw new InvalidOperationException("The embedded legacy guide catalogue was empty.");
    }
}

public sealed record LegacyGuideCatalogueEntry(
    string LegacyTrashId,
    string LegacyName,
    IReadOnlyList<string> LegacyPatterns,
    string? Movies,
    string? Tv)
{
    /// <summary>What a rule for this media type becomes, or null when nothing.</summary>
    public string? TargetFor(string? mediaType)
        => MediaPolicyCatalog.NormalizeMediaType(mediaType) == "tv" ? Tv : Movies;
}
