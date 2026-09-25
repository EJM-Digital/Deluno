using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Deluno.Tray;

/// <summary>
/// The six components the installer's picker can offer (#640): five download
/// clients and Weir. Read from the embedded copy of
/// <c>installer/bundled-components.json</c>, so what ships is exactly what the
/// manifest says -- there is no separate "the tray's own list" to drift from
/// it.
/// </summary>
public sealed record BundledComponentSpec(
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("protocol")] string? Protocol,
    [property: JsonPropertyName("licence")] string Licence,
    [property: JsonPropertyName("licenceUrl")] string LicenceUrl,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("sha256")] string? Sha256,
    [property: JsonPropertyName("minimumBytes")] long MinimumBytes,
    [property: JsonPropertyName("mustBeSigned")] bool MustBeSigned,
    [property: JsonPropertyName("silentArgs")] string? SilentArgs,
    [property: JsonPropertyName("defaultPort")] int DefaultPort,
    [property: JsonPropertyName("uninstallDisplayNameHint")] string UninstallDisplayNameHint,
    [property: JsonPropertyName("githubRepo")] string? GithubRepo,
    [property: JsonPropertyName("githubAsset")] string? GithubAsset,
    [property: JsonPropertyName("githubChecksumAsset")] string? GithubChecksumAsset)
{
    /// <summary>The manifest's own key, e.g. "qbittorrent" -- filled in by <see cref="BundledComponents.Load"/>.</summary>
    [JsonIgnore]
    public string Kind { get; init; } = string.Empty;

    public const string DownloadClientCategory = "download-client";
    public const string ProcessorCategory = "processor";
}

public static class BundledComponents
{
    private const string ResourceName = "Deluno.Tray.Assets.bundled-components.json";

    public static IReadOnlyList<BundledComponentSpec> Load()
    {
        using var stream = typeof(BundledComponents).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' was not found in {Assembly.GetExecutingAssembly().FullName}.");
        using var document = JsonDocument.Parse(stream);

        var specs = new List<BundledComponentSpec>();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Name.StartsWith('$'))
            {
                continue; // "$comment" and similar are documentation, not a component.
            }

            var spec = property.Value.Deserialize<BundledComponentSpec>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidOperationException($"Could not read the '{property.Name}' entry in bundled-components.json.");
            specs.Add(spec with { Kind = property.Name });
        }

        return specs;
    }
}
