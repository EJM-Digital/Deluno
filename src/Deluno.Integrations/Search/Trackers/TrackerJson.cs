using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Deluno.Integrations.Search.Trackers;

/// <summary>
/// JSON selectors as definitions write them (#606): a dotted path with array
/// indexes (<c>data.movies</c>, <c>files[0].name</c>, <c>$</c>), optionally
/// followed by <c>:has(path)</c>, <c>:not(path)</c> and <c>:contains(text)</c>
/// tests, which is how a definition keeps only some rows.
/// </summary>
public static partial class TrackerJson
{
    public static JsonNode? Parse(string json) => JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });

    /// <summary>The rows a selector names; an object's values when it names an object.</summary>
    public static IReadOnlyList<JsonNode> Rows(JsonNode? root, string selector)
    {
        var (path, tests) = SplitTests(selector);
        var node = Select(root, path);
        IEnumerable<JsonNode> rows = node switch
        {
            JsonArray array => array.OfType<JsonNode>(),
            JsonObject obj => obj.Select(pair => pair.Value).OfType<JsonNode>(),
            _ => []
        };

        return tests.Length == 0 ? rows.ToArray() : rows.Where(row => Passes(row, tests)).ToArray();
    }

    /// <summary>A path's node, or null where the path runs out.</summary>
    public static JsonNode? Select(JsonNode? node, string path)
    {
        var trimmed = path.Trim();
        if (trimmed.StartsWith('$'))
        {
            trimmed = trimmed[1..].TrimStart('.');
        }

        if (trimmed.Length == 0)
        {
            return node;
        }

        foreach (Match part in PathPart().Matches(trimmed))
        {
            if (node is null)
            {
                return null;
            }

            if (part.Groups["name"].Success)
            {
                node = node is JsonObject obj && obj.TryGetPropertyValue(part.Groups["name"].Value, out var child) ? child : null;
            }
            else if (part.Groups["quoted"].Success)
            {
                node = node is JsonObject obj && obj.TryGetPropertyValue(part.Groups["quoted"].Value, out var child) ? child : null;
            }
            else if (part.Groups["index"].Success)
            {
                var index = int.Parse(part.Groups["index"].Value, CultureInfo.InvariantCulture);
                node = node is JsonArray array && index >= 0 && index < array.Count ? array[index] : null;
            }
        }

        return node;
    }

    /// <summary>A field's value from a row, with the row tests applied; null when absent.</summary>
    public static string? Value(JsonNode? row, string selector)
    {
        var (path, tests) = SplitTests(selector);
        var node = Select(row, path);
        if (node is null || tests.Length > 0 && !Passes(node, tests))
        {
            return null;
        }

        return Text(node);
    }

    /// <summary>How a JSON value reads as text: booleans as .NET prints them, so "True" compares as the definitions expect.</summary>
    public static string? Text(JsonNode? node) => node switch
    {
        null => null,
        JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>(),
        JsonValue value when value.GetValueKind() == JsonValueKind.True => "True",
        JsonValue value when value.GetValueKind() == JsonValueKind.False => "False",
        JsonValue value when value.GetValueKind() == JsonValueKind.Null => null,
        JsonValue value => value.ToJsonString(),
        _ => node.ToJsonString()
    };

    public static string JoinArray(string json, string path, string separator)
    {
        var node = Select(Parse(json), path);
        return node is JsonArray array
            ? string.Join(separator, array.Select(Text))
            : Text(node) ?? string.Empty;
    }

    private static bool Passes(JsonNode node, IReadOnlyList<(string Name, string Argument)> tests)
    {
        foreach (var (name, argument) in tests)
        {
            var passes = name switch
            {
                "has" => Value(node, argument) is not null,
                "not" => Value(node, argument) is null,
                "contains" => node.ToJsonString().Contains(argument, StringComparison.Ordinal) || (Text(node) ?? string.Empty).Contains(argument, StringComparison.Ordinal),
                _ => true
            };

            if (!passes)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Splits "path:has(a):not(b:contains(c))" into the path and its tests, brackets matched.</summary>
    private static (string Path, (string Name, string Argument)[] Tests) SplitTests(string selector)
    {
        var colon = selector.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0)
        {
            return (selector, []);
        }

        var path = selector[..colon];
        var tests = new List<(string, string)>();
        var i = colon;
        while (i < selector.Length && selector[i] == ':')
        {
            var open = selector.IndexOf('(', i);
            if (open < 0)
            {
                break;
            }

            var name = selector[(i + 1)..open].Trim();
            var depth = 0;
            var close = -1;
            for (var j = open; j < selector.Length; j++)
            {
                if (selector[j] == '(')
                {
                    depth++;
                }
                else if (selector[j] == ')' && --depth == 0)
                {
                    close = j;
                    break;
                }
            }

            if (close < 0)
            {
                break;
            }

            tests.Add((name, selector[(open + 1)..close]));
            i = close + 1;
        }

        return (path, tests.ToArray());
    }

    [GeneratedRegex(@"(?:^|\.)(?<name>[^.\[\]]+)|\[(?<index>\d+)\]|\[['""](?<quoted>[^'""]+)['""]\]")]
    private static partial Regex PathPart();
}
