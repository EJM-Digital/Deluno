using System.Text.Json;
using System.Text.Json.Nodes;

namespace Deluno.Jobs.Data;

/// <summary>
/// Keeps what a grab records about itself to a size a screen can carry.
///
/// <para>A grab's notes hold the whole search plan: every release every indexer
/// returned, each with its decision. On a busy indexer that is several hundred
/// kilobytes, and the same text was stored three times (the dispatch's notes,
/// its grab response and the activity row about it), so one grab cost a
/// megabyte and a half and the Transfers and Activity screens downloaded it all
/// on every load (#1081). The decision that was made is written to its own
/// columns before this runs; what is kept here is the shape of the plan and
/// its first candidates, which is what anyone reads back.</para>
/// </summary>
public static class DispatchNotesTrimmer
{
    /// <summary>Notes at or below this many characters are left exactly as written.</summary>
    public const int TrimAboveCharacters = 32 * 1024;

    /// <summary>How many entries of any list survive a trim.</summary>
    public const int MaximumListEntries = 10;

    /// <summary>A single string longer than this (a carried torrent file, say) is cut.</summary>
    public const int MaximumStringCharacters = 2048;

    // A list that is data rather than evidence: the episodes a season pack
    // covered are read back to search for each one again, so every id stays.
    private static readonly HashSet<string> WholeLists = new(StringComparer.OrdinalIgnoreCase) { "episodeIds" };

    public static string? Trim(string? json)
    {
        if (string.IsNullOrEmpty(json) || json.Length <= TrimAboveCharacters)
        {
            return json;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return json[..TrimAboveCharacters];
        }

        if (root is null)
        {
            return json;
        }

        var trimmed = Walk(root, propertyName: null);
        return trimmed?.ToJsonString() ?? json;
    }

    private static JsonNode? Walk(JsonNode? node, string? propertyName)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var additions = new List<(string Name, int Total)>();
                foreach (var property in obj.ToArray())
                {
                    var originalCount = (property.Value as JsonArray)?.Count ?? 0;
                    var walked = Walk(property.Value, property.Key);
                    if (property.Value is JsonArray
                        && !WholeLists.Contains(property.Key)
                        && originalCount > MaximumListEntries)
                    {
                        additions.Add((property.Key + "Total", originalCount));
                    }

                    if (!ReferenceEquals(walked, property.Value))
                    {
                        obj[property.Key] = walked;
                    }
                }

                foreach (var (name, total) in additions)
                {
                    obj[name] = total;
                }

                return obj;
            }
            case JsonArray array:
            {
                if (propertyName is null || !WholeLists.Contains(propertyName))
                {
                    while (array.Count > MaximumListEntries)
                    {
                        array.RemoveAt(array.Count - 1);
                    }
                }

                for (var index = 0; index < array.Count; index++)
                {
                    var walked = Walk(array[index], propertyName: null);
                    if (!ReferenceEquals(walked, array[index]))
                    {
                        array[index] = walked;
                    }
                }

                return array;
            }
            case JsonValue value when value.TryGetValue<string>(out var text) && text.Length > MaximumStringCharacters:
                return JsonValue.Create($"{text[..MaximumStringCharacters]}... ({text.Length - MaximumStringCharacters:N0} more characters left out)");
            default:
                return node;
        }
    }
}
