using System.Text.Json;
using System.Text.Json.Nodes;

namespace Deluno.Media;

/// <summary>
/// The few fields of a recovery case's details that say which download, client entry and hand-off the case is
/// about (#1167). The list leaves the full details out (#1081) but keeps these, so a screen can still tell that
/// a case and a failed hand-off are one problem and show it once.
/// </summary>
public static class ImportRecoveryKeyDetails
{
    private static readonly string[] Keys = ["dispatchId", "handoffId", "clientId", "queueItemId"];

    /// <summary>The SQL that does <see cref="Trim"/> in the database, so a list never reads the whole details.</summary>
    public static string TrimSql(string column)
    {
        string Pick(string key)
        {
            var upper = char.ToUpperInvariant(key[0]) + key[1..];
            return $"COALESCE(json_extract({column}, '$.{upper}'), json_extract({column}, '$.{key}'), " +
                   $"json_extract({column}, '$.RetryRequest.{upper}'), json_extract({column}, '$.retryRequest.{key}'))";
        }

        var any = string.Join(", ", Keys.Select(Pick));
        var pairs = string.Join(", ", Keys.Select(key => $"'{key}', {Pick(key)}"));
        return $"CASE WHEN {column} IS NOT NULL AND json_valid({column}) THEN CASE WHEN COALESCE({any}) IS NOT NULL THEN json_object({pairs}) END END";
    }

    /// <summary>Only the key fields of the details, or null when it names none.</summary>
    public static string? Trim(string? detailsJson)
    {
        if (string.IsNullOrWhiteSpace(detailsJson))
        {
            return null;
        }

        try
        {
            if (JsonNode.Parse(detailsJson) is not JsonObject details)
            {
                return null;
            }

            var retry = (details["RetryRequest"] ?? details["retryRequest"]) as JsonObject;
            var result = new JsonObject();
            foreach (var key in Keys)
            {
                var upper = char.ToUpperInvariant(key[0]) + key[1..];
                var value = details[upper] ?? details[key] ?? retry?[upper] ?? retry?[key];
                if (value is JsonValue json && json.TryGetValue<string>(out var text) && text.Length > 0)
                {
                    result[key] = text;
                }
            }

            return result.Count == 0 ? null : result.ToJsonString();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
