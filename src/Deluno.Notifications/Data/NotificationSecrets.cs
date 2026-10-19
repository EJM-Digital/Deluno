using System.Text.Json;
using System.Text.Json.Nodes;

namespace Deluno.Notifications.Data;

/// <summary>
/// #1364: what is secret in a notification connection, and how it is shown. A Discord or Slack address
/// carries its token in the path and Telegram, Pushover, Gotify and ntfy keep theirs in the connection's
/// settings, so both are stored protected and every list and read shows only a mask. A save that sends the
/// mask back keeps the stored value.
/// </summary>
public static class NotificationSecrets
{
    public const string Mask = "\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022";

    public const string UrlPurpose = "notification-webhook:url";

    public const string ConfigPurpose = "notification-webhook:config";

    private static readonly string[] SecretKeys = ["botToken", "appToken", "userKey", "accessToken"];

    /// <summary>The address as the screen may see it: scheme and host, then the mask when there is anything after them.</summary>
    public static string MaskUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return Mask;
        }

        var hasMore = uri.PathAndQuery.Length > 1 || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment);
        return $"{uri.Scheme}://{uri.Authority}/{(hasMore ? Mask : string.Empty)}";
    }

    /// <summary>The scheme, host and port only, for a log line.</summary>
    public static string HostOnly(string? url)
        => Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) ? uri.Authority : "(no address)";

    /// <summary>The settings with each token replaced by the mask; names and chat ids stay readable.</summary>
    public static string? MaskConfig(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
        {
            return null;
        }

        if (Parse(configJson) is not { } node)
        {
            return Mask;
        }

        foreach (var key in SecretKeys)
        {
            if (FindKey(node, key) is { } found && !string.IsNullOrEmpty(node[found]?.GetValue<string>()))
            {
                node[found] = Mask;
            }
        }

        return node.ToJsonString();
    }

    /// <summary>The settings to store: any token still showing the mask takes the value that was stored.</summary>
    public static string? KeepMaskedConfig(string? incoming, string? storedClear)
    {
        if (string.IsNullOrWhiteSpace(incoming) || Parse(incoming) is not { } node)
        {
            return incoming;
        }

        var stored = Parse(storedClear);
        foreach (var key in SecretKeys)
        {
            if (FindKey(node, key) is { } found && node[found]?.GetValue<string>() == Mask)
            {
                var old = stored is null ? null : FindKey(stored, key) is { } storedKey ? stored[storedKey]?.GetValue<string>() : null;
                if (string.IsNullOrEmpty(old))
                {
                    node.Remove(found);
                }
                else
                {
                    node[found] = old;
                }
            }
        }

        return node.Count == 0 ? null : node.ToJsonString();
    }

    /// <summary>The address to store: the mask a list handed out means "keep what is stored".</summary>
    public static string KeepMaskedUrl(string? incoming, string? storedClear)
    {
        var trimmed = incoming?.Trim() ?? string.Empty;
        return !string.IsNullOrEmpty(storedClear) && string.Equals(trimmed, MaskUrl(storedClear), StringComparison.Ordinal)
            ? storedClear
            : trimmed;
    }

    private static JsonObject? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? FindKey(JsonObject node, string key)
        => node.Select(pair => pair.Key).FirstOrDefault(name => string.Equals(name, key, StringComparison.OrdinalIgnoreCase));
}
