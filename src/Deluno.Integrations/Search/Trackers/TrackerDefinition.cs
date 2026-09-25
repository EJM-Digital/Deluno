using System.Globalization;
using YamlDotNet.RepresentationModel;

namespace Deluno.Integrations.Search.Trackers;

/// <summary>
/// One tracker Deluno can search itself (#606), read from a definition
/// written in Deluno's own tracker definition format: see
/// docs/tracker-definitions.md for the fields it supports.
///
/// <para>The model is read from the YAML node tree by hand rather than by a
/// deserializer, for two reasons. Field order matters (a field may read an
/// earlier field's value through <c>.Result</c>), and the format is loose about
/// types: a filter's args are a scalar or a list, a category id a number or a
/// string, a default any scalar.</para>
/// </summary>
public sealed class TrackerDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = string.Empty;
    public string Language { get; init; } = "en-US";

    /// <summary>public, semi-private or private.</summary>
    public string Type { get; init; } = "private";
    public string Encoding { get; init; } = "UTF-8";
    public bool FollowRedirect { get; init; }
    public double? RequestDelaySeconds { get; init; }
    public IReadOnlyList<string> Links { get; init; } = [];
    public IReadOnlyList<string> LegacyLinks { get; init; } = [];
    public TrackerCaps Caps { get; init; } = new();
    public IReadOnlyList<TrackerSetting> Settings { get; init; } = [];
    public TrackerLogin? Login { get; init; }
    public required TrackerSearch Search { get; init; }
    public TrackerDownload? Download { get; init; }

    public bool IsPublic => string.Equals(Type, "public", StringComparison.OrdinalIgnoreCase);

    public static TrackerDefinition Parse(string yaml)
    {
        var stream = new YamlStream();
        using (var reader = new StringReader(yaml))
        {
            stream.Load(reader);
        }

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw new FormatException("A tracker definition must be a YAML mapping.");
        }

        return new TrackerDefinition
        {
            Id = Yaml.RequiredString(root, "id"),
            Name = Yaml.RequiredString(root, "name"),
            Description = Yaml.String(root, "description") ?? string.Empty,
            Language = Yaml.String(root, "language") ?? "en-US",
            Type = Yaml.String(root, "type") ?? "private",
            Encoding = Yaml.String(root, "encoding") ?? "UTF-8",
            FollowRedirect = Yaml.Bool(root, "followredirect") ?? false,
            RequestDelaySeconds = Yaml.Double(root, "requestDelay"),
            Links = Yaml.StringList(root, "links"),
            LegacyLinks = Yaml.StringList(root, "legacylinks"),
            Caps = TrackerCaps.From(Yaml.Mapping(root, "caps")),
            Settings = Yaml.Sequence(root, "settings").OfType<YamlMappingNode>().Select(TrackerSetting.From).ToArray(),
            Login = Yaml.Mapping(root, "login") is { } login ? TrackerLogin.From(login) : null,
            Search = TrackerSearch.From(Yaml.Mapping(root, "search") ?? throw new FormatException("A tracker definition needs a search block.")),
            Download = Yaml.Mapping(root, "download") is { } download ? TrackerDownload.From(download) : null
        };
    }
}

public sealed class TrackerCaps
{
    public IReadOnlyList<TrackerCategoryMapping> CategoryMappings { get; init; } = [];
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Modes { get; init; } = new Dictionary<string, IReadOnlyList<string>>();
    public bool AllowRawSearch { get; init; }

    internal static TrackerCaps From(YamlMappingNode? node)
    {
        if (node is null)
        {
            return new TrackerCaps();
        }

        var mappings = new List<TrackerCategoryMapping>();
        foreach (var item in Yaml.Sequence(node, "categorymappings").OfType<YamlMappingNode>())
        {
            mappings.Add(new TrackerCategoryMapping(
                Yaml.String(item, "id") ?? string.Empty,
                Yaml.String(item, "cat") ?? string.Empty,
                Yaml.String(item, "desc"),
                Yaml.Bool(item, "default") ?? false));
        }

        // The older form: a plain map of site id to category name.
        if (Yaml.Mapping(node, "categories") is { } categories)
        {
            foreach (var (key, value) in categories.Children)
            {
                mappings.Add(new TrackerCategoryMapping(Yaml.Scalar(key) ?? string.Empty, Yaml.Scalar(value) ?? string.Empty, null, false));
            }
        }

        var modes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (Yaml.Mapping(node, "modes") is { } modesNode)
        {
            foreach (var (key, value) in modesNode.Children)
            {
                modes[Yaml.Scalar(key) ?? string.Empty] = value is YamlSequenceNode sequence
                    ? sequence.Children.Select(Yaml.Scalar).OfType<string>().ToArray()
                    : [];
            }
        }

        return new TrackerCaps
        {
            CategoryMappings = mappings,
            Modes = modes,
            AllowRawSearch = Yaml.Bool(node, "allowrawsearch") ?? false
        };
    }

    public bool Supports(string mode, string parameter)
        => Modes.TryGetValue(mode, out var parameters)
           && parameters.Contains(parameter, StringComparer.OrdinalIgnoreCase);
}

public sealed record TrackerCategoryMapping(string SiteId, string Category, string? Description, bool IsDefault);

public sealed class TrackerSetting
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public string? Label { get; init; }
    public string? Default { get; init; }
    public IReadOnlyList<string> Defaults { get; init; } = [];

    /// <summary>A select's choices: the value sent to the site, and the label shown.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Options { get; init; } = [];

    /// <summary>Settings that hold a secret and are never shown back or logged.</summary>
    public bool IsSecret => Type is "password" || Name is "apikey" or "cookie" or "passkey" or "rsskey" or "pin" or "2facode";

    /// <summary>Settings that only explain something and store nothing.</summary>
    public bool IsInformational => Type.StartsWith("info", StringComparison.OrdinalIgnoreCase);

    internal static TrackerSetting From(YamlMappingNode node)
    {
        var options = new List<KeyValuePair<string, string>>();
        if (Yaml.Mapping(node, "options") is { } optionsNode)
        {
            foreach (var (key, value) in optionsNode.Children)
            {
                options.Add(new(Yaml.Scalar(key) ?? string.Empty, Yaml.Scalar(value) ?? string.Empty));
            }
        }

        return new TrackerSetting
        {
            Name = Yaml.RequiredString(node, "name"),
            Type = Yaml.String(node, "type") ?? "text",
            Label = Yaml.String(node, "label"),
            Default = Yaml.String(node, "default"),
            Defaults = Yaml.StringList(node, "defaults"),
            Options = options
        };
    }
}

public sealed class TrackerLogin
{
    public string? Method { get; init; }
    public string? Path { get; init; }
    public string? SubmitPath { get; init; }
    public string? Form { get; init; }
    public IReadOnlyList<string> Cookies { get; init; } = [];
    public IReadOnlyList<KeyValuePair<string, string>> Inputs { get; init; } = [];
    public IReadOnlyList<KeyValuePair<string, TrackerSelector>> SelectorInputs { get; init; } = [];
    public IReadOnlyList<KeyValuePair<string, TrackerSelector>> GetSelectorInputs { get; init; } = [];
    public bool Selectors { get; init; }
    public IReadOnlyList<TrackerError> Errors { get; init; } = [];
    public TrackerPageTest? Test { get; init; }
    public bool HasCaptcha { get; init; }
    public IReadOnlyList<KeyValuePair<string, IReadOnlyList<string>>> Headers { get; init; } = [];

    internal static TrackerLogin From(YamlMappingNode node) => new()
    {
        Method = Yaml.String(node, "method"),
        Path = Yaml.String(node, "path"),
        SubmitPath = Yaml.String(node, "submitpath"),
        Form = Yaml.String(node, "form"),
        Cookies = Yaml.StringList(node, "cookies"),
        Inputs = Yaml.StringPairs(Yaml.Mapping(node, "inputs")),
        SelectorInputs = Yaml.SelectorPairs(Yaml.Mapping(node, "selectorinputs")),
        GetSelectorInputs = Yaml.SelectorPairs(Yaml.Mapping(node, "getselectorinputs")),
        Selectors = Yaml.Bool(node, "selectors") ?? false,
        Errors = Yaml.Sequence(node, "error").OfType<YamlMappingNode>().Select(TrackerError.From).ToArray(),
        Test = Yaml.Mapping(node, "test") is { } test ? new TrackerPageTest(Yaml.String(test, "path"), Yaml.String(test, "selector")) : null,
        HasCaptcha = Yaml.Mapping(node, "captcha") is not null,
        Headers = Yaml.HeaderPairs(Yaml.Mapping(node, "headers"))
    };
}

public sealed record TrackerPageTest(string? Path, string? Selector);

public sealed record TrackerError(string? Path, string Selector, TrackerSelector? Message)
{
    internal static TrackerError From(YamlMappingNode node)
        => new(Yaml.String(node, "path"), Yaml.String(node, "selector") ?? string.Empty,
            Yaml.Mapping(node, "message") is { } message ? TrackerSelector.From(message) : null);
}

public sealed class TrackerSearch
{
    public IReadOnlyList<TrackerSearchPath> Paths { get; init; } = [];
    public IReadOnlyList<KeyValuePair<string, string>> Inputs { get; init; } = [];
    public IReadOnlyList<KeyValuePair<string, IReadOnlyList<string>>> Headers { get; init; } = [];
    public IReadOnlyList<TrackerFilter> KeywordsFilters { get; init; } = [];
    public IReadOnlyList<TrackerFilter> PreprocessingFilters { get; init; } = [];
    public IReadOnlyList<TrackerError> Errors { get; init; } = [];
    public bool AllowEmptyInputs { get; init; }
    public required TrackerRows Rows { get; init; }

    /// <summary>In definition order: a field may read any field before it.</summary>
    public required IReadOnlyList<KeyValuePair<string, TrackerSelector>> Fields { get; init; }

    internal static TrackerSearch From(YamlMappingNode node)
    {
        var paths = Yaml.Sequence(node, "paths").OfType<YamlMappingNode>().Select(TrackerSearchPath.From).ToList();
        if (paths.Count == 0 && Yaml.String(node, "path") is { } single)
        {
            paths.Add(new TrackerSearchPath { Path = single });
        }

        return new TrackerSearch
        {
            Paths = paths,
            Inputs = Yaml.StringPairs(Yaml.Mapping(node, "inputs")),
            Headers = Yaml.HeaderPairs(Yaml.Mapping(node, "headers")),
            KeywordsFilters = Yaml.Filters(node, "keywordsfilters"),
            PreprocessingFilters = Yaml.Filters(node, "preprocessingfilters"),
            Errors = Yaml.Sequence(node, "error").OfType<YamlMappingNode>().Select(TrackerError.From).ToArray(),
            AllowEmptyInputs = Yaml.Bool(node, "allowEmptyInputs") ?? false,
            Rows = TrackerRows.From(Yaml.Mapping(node, "rows") ?? throw new FormatException("A tracker search needs rows.")),
            Fields = Yaml.SelectorPairs(Yaml.Mapping(node, "fields") ?? throw new FormatException("A tracker search needs fields."))
        };
    }
}

public sealed class TrackerSearchPath
{
    public required string Path { get; init; }
    public string? Method { get; init; }
    public bool? FollowRedirect { get; init; }
    public IReadOnlyList<string> Categories { get; init; } = [];
    public IReadOnlyList<KeyValuePair<string, string>> Inputs { get; init; } = [];
    public bool InheritInputs { get; init; } = true;
    public string? QuerySeparator { get; init; }

    /// <summary>html, json or xml.</summary>
    public string ResponseType { get; init; } = "html";
    public string? NoResultsMessage { get; init; }

    internal static TrackerSearchPath From(YamlMappingNode node)
    {
        var response = Yaml.Mapping(node, "response");
        return new TrackerSearchPath
        {
            Path = Yaml.String(node, "path") ?? string.Empty,
            Method = Yaml.String(node, "method"),
            FollowRedirect = Yaml.Bool(node, "followredirect"),
            Categories = Yaml.StringList(node, "categories"),
            Inputs = Yaml.StringPairs(Yaml.Mapping(node, "inputs")),
            InheritInputs = Yaml.Bool(node, "inheritinputs") ?? true,
            QuerySeparator = Yaml.String(node, "queryseparator"),
            ResponseType = (response is null ? null : Yaml.String(response, "type"))?.ToLowerInvariant() ?? "html",
            NoResultsMessage = response is null ? null : Yaml.String(response, "noResultsMessage")
        };
    }
}

public sealed class TrackerRows
{
    public string? Selector { get; init; }
    public string? Attribute { get; init; }
    public bool Multiple { get; init; }
    public bool MissingAttributeEqualsNoResults { get; init; }
    public int After { get; init; }
    public TrackerSelector? DateHeaders { get; init; }
    public TrackerSelector? Count { get; init; }
    public string? Remove { get; init; }
    public IReadOnlyList<TrackerFilter> Filters { get; init; } = [];

    internal static TrackerRows From(YamlMappingNode node) => new()
    {
        Selector = Yaml.String(node, "selector"),
        Attribute = Yaml.String(node, "attribute"),
        Multiple = Yaml.Bool(node, "multiple") ?? false,
        MissingAttributeEqualsNoResults = Yaml.Bool(node, "missingAttributeEqualsNoResults") ?? false,
        After = (int)(Yaml.Double(node, "after") ?? 0),
        DateHeaders = Yaml.Mapping(node, "dateheaders") is { } headers ? TrackerSelector.From(headers) : null,
        Count = Yaml.Mapping(node, "count") is { } count ? TrackerSelector.From(count) : null,
        Remove = Yaml.String(node, "remove"),
        Filters = Yaml.Filters(node, "filters")
    };
}

/// <summary>How to read one value: a selector and its attribute, or a template, then filters.</summary>
public sealed class TrackerSelector
{
    public string? Selector { get; init; }
    public string? Attribute { get; init; }
    public string? Text { get; init; }
    public bool Optional { get; init; }
    public string? Default { get; init; }
    public string? Remove { get; init; }
    public bool UseBeforeResponse { get; init; }

    /// <summary>A selector (HTML) or value (JSON) and what it means, first match wins; "*" matches anything.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Case { get; init; } = [];
    public IReadOnlyList<TrackerFilter> Filters { get; init; } = [];

    internal static TrackerSelector From(YamlNode node)
    {
        if (node is YamlScalarNode scalar)
        {
            // A bare string is shorthand for a selector.
            return new TrackerSelector { Selector = scalar.Value };
        }

        if (node is not YamlMappingNode mapping)
        {
            return new TrackerSelector();
        }

        return new TrackerSelector
        {
            Selector = Yaml.String(mapping, "selector"),
            Attribute = Yaml.String(mapping, "attribute"),
            Text = Yaml.String(mapping, "text"),
            Optional = Yaml.Bool(mapping, "optional") ?? false,
            Default = Yaml.String(mapping, "default"),
            Remove = Yaml.String(mapping, "remove"),
            UseBeforeResponse = Yaml.Bool(mapping, "usebeforeresponse") ?? false,
            Case = Yaml.StringPairs(Yaml.Mapping(mapping, "case")),
            Filters = Yaml.Filters(mapping, "filters")
        };
    }
}

public sealed record TrackerFilter(string Name, IReadOnlyList<string> Args);

public sealed class TrackerDownload
{
    public IReadOnlyList<TrackerSelector> Selectors { get; init; } = [];
    public string? Method { get; init; }
    public TrackerDownloadBefore? Before { get; init; }
    public TrackerSelector? InfoHash { get; init; }
    public TrackerSelector? InfoHashTitle { get; init; }
    public IReadOnlyList<KeyValuePair<string, IReadOnlyList<string>>> Headers { get; init; } = [];

    internal static TrackerDownload From(YamlMappingNode node)
    {
        var infohash = Yaml.Mapping(node, "infohash");
        var before = Yaml.Mapping(node, "before");
        return new TrackerDownload
        {
            Selectors = Yaml.Sequence(node, "selectors").Select(TrackerSelector.From).ToArray(),
            Method = Yaml.String(node, "method"),
            Before = before is null ? null : new TrackerDownloadBefore(
                Yaml.String(before, "path"),
                Yaml.Mapping(before, "pathselector") is { } pathSelector ? TrackerSelector.From(pathSelector) : null,
                Yaml.String(before, "method"),
                Yaml.StringPairs(Yaml.Mapping(before, "inputs")),
                Yaml.String(before, "queryseparator")),
            InfoHash = infohash is not null && Yaml.Mapping(infohash, "hash") is { } hash ? TrackerSelector.From(hash) : null,
            InfoHashTitle = infohash is not null && Yaml.Mapping(infohash, "title") is { } title ? TrackerSelector.From(title) : null,
            Headers = Yaml.HeaderPairs(Yaml.Mapping(node, "headers"))
        };
    }
}

public sealed record TrackerDownloadBefore(
    string? Path,
    TrackerSelector? PathSelector,
    string? Method,
    IReadOnlyList<KeyValuePair<string, string>> Inputs,
    string? QuerySeparator);

/// <summary>Reading the loosely typed YAML node tree.</summary>
internal static class Yaml
{
    public static YamlNode? Child(YamlMappingNode node, string key)
        => node.Children.TryGetValue(new YamlScalarNode(key), out var child) ? child : null;

    public static string? Scalar(YamlNode? node)
        => node is YamlScalarNode scalar ? scalar.Value : null;

    public static string? String(YamlMappingNode node, string key) => Child(node, key) switch
    {
        YamlScalarNode scalar => scalar.Value,
        // A list where one value is expected: the first is what is meant.
        YamlSequenceNode sequence => sequence.Children.Select(Scalar).FirstOrDefault(),
        _ => null
    };

    public static string RequiredString(YamlMappingNode node, string key)
        => String(node, key) is { Length: > 0 } value ? value : throw new FormatException($"A tracker definition needs '{key}'.");

    public static bool? Bool(YamlMappingNode node, string key)
        => String(node, key) is { } value && bool.TryParse(value, out var parsed) ? parsed : null;

    public static double? Double(YamlMappingNode node, string key)
        => String(node, key) is { } value && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    public static YamlMappingNode? Mapping(YamlMappingNode node, string key) => Child(node, key) as YamlMappingNode;

    public static IEnumerable<YamlNode> Sequence(YamlMappingNode node, string key)
        => Child(node, key) is YamlSequenceNode sequence ? sequence.Children : [];

    public static IReadOnlyList<string> StringList(YamlMappingNode node, string key) => Child(node, key) switch
    {
        YamlSequenceNode sequence => sequence.Children.Select(Scalar).OfType<string>().ToArray(),
        YamlScalarNode scalar when scalar.Value is not null => [scalar.Value],
        _ => []
    };

    public static IReadOnlyList<KeyValuePair<string, string>> StringPairs(YamlMappingNode? node)
        => node is null
            ? []
            : node.Children.Select(pair => new KeyValuePair<string, string>(Scalar(pair.Key) ?? string.Empty, Scalar(pair.Value) ?? string.Empty)).ToArray();

    public static IReadOnlyList<KeyValuePair<string, TrackerSelector>> SelectorPairs(YamlMappingNode? node)
        => node is null
            ? []
            : node.Children.Select(pair => new KeyValuePair<string, TrackerSelector>(Scalar(pair.Key) ?? string.Empty, TrackerSelector.From(pair.Value))).ToArray();

    public static IReadOnlyList<KeyValuePair<string, IReadOnlyList<string>>> HeaderPairs(YamlMappingNode? node)
        => node is null
            ? []
            : node.Children.Select(pair => new KeyValuePair<string, IReadOnlyList<string>>(
                Scalar(pair.Key) ?? string.Empty,
                pair.Value is YamlSequenceNode values ? values.Children.Select(Scalar).OfType<string>().ToArray() : Scalar(pair.Value) is { } one ? [one] : [])).ToArray();

    public static IReadOnlyList<TrackerFilter> Filters(YamlMappingNode node, string key)
        => Sequence(node, key).OfType<YamlMappingNode>().Select(filter => new TrackerFilter(
            String(filter, "name") ?? string.Empty,
            Child(filter, "args") switch
            {
                YamlSequenceNode sequence => sequence.Children.Select(child => Scalar(child) ?? string.Empty).ToArray(),
                YamlScalarNode scalar => [scalar.Value ?? string.Empty],
                _ => []
            })).ToArray();
}
