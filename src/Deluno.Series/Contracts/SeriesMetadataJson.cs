using System.Text.Json;

namespace Deluno.Series.Contracts;

/// <summary>
/// Reads a field the metadata provider recorded in a series' stored provider
/// blob (<see cref="SeriesListItem.MetadataJson"/>) but that does not have its
/// own column. Series does not persist every provider fact as a dedicated
/// column — <c>TvDbId</c> among them — so a caller that only needs one of
/// them reads it back out of the blob it already holds rather than adding a
/// column for each.
/// </summary>
public static class SeriesMetadataJson
{
    /// <summary>
    /// The TVDB identifier TMDb's external-ids lookup returned when this series
    /// was added or last refreshed, or null when the provider had none (common:
    /// not every TMDb title is also on TVDB) or the blob predates that field.
    /// </summary>
    public static string? ReadTvDbId(string? metadataJson) => ReadText(metadataJson, "TvDbId", "tvdbId", "tvdb_id");

    private static string? ReadText(string? metadataJson, params string[] names)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(metadataJson);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (names.Any(name => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) &&
                    property.Value.ValueKind == JsonValueKind.String)
                {
                    return property.Value.GetString();
                }
            }
        }
        catch (JsonException)
        {
            // Provider metadata is optional and may be an older/manual blob.
        }

        return null;
    }
}
