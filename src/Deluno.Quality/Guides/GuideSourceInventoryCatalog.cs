using System.Text.Json;
using Deluno.Quality.ReleasePreferences;

namespace Deluno.Quality.Guides;

/// <summary>
/// Loads the pinned upstream source inventory. It has no network path: release
/// decisions use only package content already embedded in the server assembly.
/// </summary>
public static class GuideSourceInventoryCatalog
{
    private const string ResourceName = "Deluno.Quality.Guides.trash-guide-source-inventory.json";

    public static GuideSourceInventory Current { get; } = Load();

    private static GuideSourceInventory Load()
    {
        using var stream = typeof(GuideSourceInventoryCatalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded guide source inventory '{ResourceName}' was not found.");
        return JsonSerializer.Deserialize<GuideSourceInventory>(stream, ReleasePreferenceJson.Options)
            ?? throw new InvalidOperationException("The embedded guide source inventory was empty.");
    }
}
