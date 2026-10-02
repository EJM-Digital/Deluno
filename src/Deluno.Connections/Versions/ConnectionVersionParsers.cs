using System.Text.Json;

namespace Deluno.Connections.Versions;

/// <summary>
/// Reads a version out of what a connection answered (#1117). Every method returns null for an answer it
/// does not recognise: a version Deluno cannot read is "unknown", never an error and never a guess.
/// </summary>
public static class ConnectionVersionParsers
{
    /// <summary>
    /// "4.6.7", "v4.6.7", "4.0.5 (a6fe2de319)" and "1.43.4.10903-e5521bd8c" become the first word with any
    /// leading "v" dropped; anything that does not start with a number is not a version.
    /// </summary>
    public static string? Clean(string? raw)
    {
        var text = raw?.Trim().Trim('"', '\'');
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var word = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (word is null)
        {
            return null;
        }

        if (word.Length > 1 && (word[0] is 'v' or 'V') && char.IsDigit(word[1]))
        {
            word = word[1..];
        }

        return word.Length is > 0 and <= 40 && char.IsDigit(word[0]) ? word : null;
    }

    /// <summary>qBittorrent's <c>api/v2/app/version</c> answers the bare text "v4.6.7".</summary>
    public static string? FromQbittorrent(string? body) => Clean(body);

    /// <summary>Transmission's <c>session-get</c>: <c>arguments.version</c>, "4.0.5 (a6fe2de319)".</summary>
    public static string? FromTransmission(string? json)
        => Clean(Read(json, root => Property(Property(root, "arguments"), "version")));

    /// <summary>Deluge's <c>daemon.info</c> (or <c>daemon.get_version</c>): the version string in <c>result</c>.</summary>
    public static string? FromDeluge(string? json)
        => Clean(Read(json, root => Property(root, "result")));

    /// <summary>Plex's <c>/identity</c> (JSON): <c>MediaContainer.version</c>, "1.43.4.10903-e5521bd8c".</summary>
    public static string? FromPlexIdentity(string? json)
        => Clean(Read(json, root => Property(Property(root, "MediaContainer"), "version")));

    /// <summary>The <c>Version</c> of Jellyfin's and Emby's <c>/System/Info</c> or <c>/System/Info/Public</c>.</summary>
    public static string? FromEmbyFamily(string? json)
        => Clean(Read(json, root => Property(root, "Version")));

    /// <summary>Seerr's <c>/api/v1/status</c>: <c>version</c>.</summary>
    public static string? FromSeerr(string? json)
        => Clean(Read(json, root => Property(root, "version")));

    /// <summary>
    /// A processor's <c>GET /api/v1/intake/capabilities</c> answer: the optional <c>version</c> ("X.Y.Z", or
    /// "0.0.1-dev" for a development build). Null when absent. Generic on purpose: any processor may send it.
    /// </summary>
    public static string? FromProcessorCapabilities(string? json)
        => Clean(Read(json, root => Property(root, "version")));


    private static string? Read(string? json, Func<JsonElement?, JsonElement?> select)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            using var document = JsonDocument.Parse(json);
            var element = select(document.RootElement);
            return element is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement? Property(JsonElement? element, string name)
        => element is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var found) ? found : null;
}
