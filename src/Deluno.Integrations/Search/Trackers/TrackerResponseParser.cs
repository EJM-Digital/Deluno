using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using AngleSharp.Xml.Parser;

namespace Deluno.Integrations.Search.Trackers;

/// <summary>
/// Reads one search page into releases (#606): HTML and XML through CSS
/// selectors, JSON through paths, then every field in definition order.
///
/// <para>A row whose required field cannot be read is skipped and counted,
/// not fatal; a site that changed one row's markup still returns the rest.</para>
/// </summary>
public static partial class TrackerResponseParser
{
    public static TrackerSearchResult Parse(
        TrackerDefinition definition,
        TrackerSearchPath path,
        string body,
        Uri pageUri,
        IReadOnlyDictionary<string, object?> variables,
        TrackerContext context)
    {
        var search = definition.Search;
        if (search.PreprocessingFilters.Count > 0)
        {
            body = TrackerFilters.Apply(body, search.PreprocessingFilters, variables, context);
        }

        if (path.NoResultsMessage is { } noResults
            && (string.Equals(body.Trim(), noResults, StringComparison.Ordinal) || noResults.Length == 0 && string.IsNullOrWhiteSpace(body)))
        {
            return new TrackerSearchResult([], [], 0);
        }

        return path.ResponseType == "json"
            ? ParseJson(definition, body, pageUri, variables, context)
            : ParseMarkup(definition, path.ResponseType == "xml", body, pageUri, variables, context);
    }

    // ---- HTML and XML ------------------------------------------------------

    /// <summary>
    /// A feed behind Cloudflare can arrive with a <c>&lt;script&gt;</c> appended
    /// after its closing root tag (seen on torrentdownloads.pro's search RSS,
    /// #606), which no XML parser accepts. Everything after the root element's
    /// own closing tag is dropped; a document without one is left alone.
    /// </summary>
    internal static string WithoutTrailingContent(string body)
    {
        // The first element tag; the XML declaration and comments start with
        // <? and <!, which the letter this requires already rules out.
        var match = Regex.Match(body, @"<([A-Za-z_][\w:.-]*)[\s>/]", RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return body;
        }

        var closing = $"</{match.Groups[1].Value}>";
        var end = body.LastIndexOf(closing, StringComparison.Ordinal);
        return end < 0 ? body : body[..(end + closing.Length)];
    }

    private static TrackerSearchResult ParseMarkup(
        TrackerDefinition definition,
        bool isXml,
        string body,
        Uri pageUri,
        IReadOnlyDictionary<string, object?> variables,
        TrackerContext context)
    {
        var search = definition.Search;
        IDocument document = isXml ? new XmlParser().ParseDocument(WithoutTrailingContent(body)) : new HtmlParser().ParseDocument(body);
        var root = document.DocumentElement;

        CheckForErrors(search.Errors, root, variables, context);

        var rowSelector = TrackerTemplate.Render(search.Rows.Selector, variables);
        var rows = root.QuerySelectorAll(rowSelector).ToList();
        if (search.Rows.After > 0)
        {
            rows = MergeFollowingRows(rows, search.Rows.After);
        }

        var releases = new List<TrackerRelease>();
        var skipped = 0;
        var problems = new List<string>();
        foreach (var row in rows)
        {
            if (search.Rows.Remove is { Length: > 0 } remove)
            {
                foreach (var element in row.QuerySelectorAll(remove).ToArray())
                {
                    element.Remove();
                }
            }

            var rowVariables = new Dictionary<string, object?>(variables);
            try
            {
                var release = ReadFields(
                    definition,
                    rowVariables,
                    pageUri,
                    context,
                    (selector, required) => HtmlValue(selector, row, rowVariables, context, required));

                if (search.Rows.DateHeaders is { } dateHeaders && release.PublishDate is null
                    && DateFromHeader(dateHeaders, row, rowVariables, context) is { } headerDate)
                {
                    release.PublishDate = headerDate;
                }

                if (Keep(definition, release, variables))
                {
                    releases.Add(release);
                }
            }
            catch (Exception exception) when (exception is FormatException or TrackerFieldException or NotSupportedException or DomException or ArgumentException or RegexMatchTimeoutException)
            {
                skipped++;
                if (problems.Count < 3)
                {
                    problems.Add(exception.Message);
                }
            }
        }

        return new TrackerSearchResult(releases, problems, skipped);
    }

    private static List<IElement> MergeFollowingRows(List<IElement> rows, int after)
    {
        var merged = new List<IElement>();
        for (var i = 0; i < rows.Count; i += after + 1)
        {
            var row = (IElement)rows[i].Clone();
            for (var j = 1; j <= after && i + j < rows.Count; j++)
            {
                foreach (var child in rows[i + j].ChildNodes.ToArray())
                {
                    row.AppendChild(child.Clone());
                }
            }

            merged.Add(row);
        }

        return merged;
    }

    private static DateTimeOffset? DateFromHeader(TrackerSelector headers, IElement row, Dictionary<string, object?> variables, TrackerContext context)
    {
        var selector = TrackerTemplate.Render(headers.Selector, variables);
        for (var previous = row.PreviousElementSibling; previous is not null; previous = previous.PreviousElementSibling)
        {
            if (!Matches(previous, selector))
            {
                continue;
            }

            var value = HtmlValue(headers, previous, variables, context, required: false, matchSelf: true);
            try
            {
                return value is null ? null : TrackerDates.FromUnknown(value, context.Now);
            }
            catch (FormatException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>Cardigann's reading of one selector against a row: the row itself if it matches, else its first descendant that does.</summary>
    internal static string? HtmlValue(
        TrackerSelector selector,
        IElement row,
        IReadOnlyDictionary<string, object?> variables,
        TrackerContext context,
        bool required,
        bool matchSelf = false)
    {
        if (selector.Text is not null)
        {
            return TrackerFilters.Apply(TrackerTemplate.Render(selector.Text, variables), selector.Filters, variables, context);
        }

        var selection = row;
        if (!string.IsNullOrEmpty(selector.Selector) && !matchSelf)
        {
            var css = TrackerTemplate.Render(selector.Selector, variables);
            selection = Matches(row, css) ? row : row.QuerySelector(css);
            if (selection is null)
            {
                return required ? throw new TrackerFieldException($"Nothing matched \"{css}\".") : null;
            }
        }

        if (selector.Remove is { Length: > 0 } remove)
        {
            foreach (var element in selection.QuerySelectorAll(remove).ToArray())
            {
                element.Remove();
            }
        }

        string? value;
        if (selector.Case.Count > 0)
        {
            value = null;
            foreach (var (test, meaning) in selector.Case)
            {
                if (Matches(selection, test) || selection.QuerySelector(test) is not null)
                {
                    value = meaning;
                    break;
                }
            }

            if (value is null)
            {
                return required ? throw new TrackerFieldException("No case matched.") : null;
            }
        }
        else if (selector.Attribute is not null)
        {
            value = selection.GetAttribute(selector.Attribute);
            if (value is null)
            {
                return required ? throw new TrackerFieldException($"The attribute \"{selector.Attribute}\" is missing.") : null;
            }
        }
        else
        {
            value = selection.TextContent;
        }

        return TrackerFilters.Apply(NormalizeSpace(value), selector.Filters, variables, context);
    }

    internal static bool Matches(IElement element, string selector)
    {
        if (selector == "*")
        {
            return true;
        }

        try
        {
            return element.Matches(selector);
        }
        catch (DomException)
        {
            return false;
        }
    }

    internal static void CheckForErrors(IReadOnlyList<TrackerError> errors, IElement root, IReadOnlyDictionary<string, object?> variables, TrackerContext context)
    {
        foreach (var error in errors)
        {
            if (string.IsNullOrEmpty(error.Selector) || root.QuerySelector(error.Selector) is not { } found)
            {
                continue;
            }

            var message = error.Message is null
                ? NormalizeSpace(found.TextContent)
                : HtmlValue(error.Message, root, variables, context, required: false) ?? NormalizeSpace(found.TextContent);
            throw new TrackerSiteException(string.IsNullOrWhiteSpace(message) ? "The tracker reported an error." : message);
        }
    }

    // ---- JSON --------------------------------------------------------------

    private static TrackerSearchResult ParseJson(
        TrackerDefinition definition,
        string body,
        Uri pageUri,
        IReadOnlyDictionary<string, object?> variables,
        TrackerContext context)
    {
        var search = definition.Search;
        var root = TrackerJson.Parse(body);

        if (search.Rows.Count is { } count
            && JsonValue(count, root, root, variables, context, required: false) is { } counted
            && int.TryParse(counted, NumberStyles.Integer, CultureInfo.InvariantCulture, out var total)
            && total < 1)
        {
            return new TrackerSearchResult([], [], 0);
        }

        var rowSelector = TrackerTemplate.Render(search.Rows.Selector ?? "$", variables);
        var releases = new List<TrackerRelease>();
        var skipped = 0;
        var problems = new List<string>();
        foreach (var row in TrackerJson.Rows(root, rowSelector))
        {
            IEnumerable<JsonNode> items = [row];
            if (!string.IsNullOrEmpty(search.Rows.Attribute))
            {
                var inner = TrackerJson.Select(row, search.Rows.Attribute);
                if (inner is null)
                {
                    if (!search.Rows.MissingAttributeEqualsNoResults)
                    {
                        skipped++;
                    }

                    continue;
                }

                items = search.Rows.Multiple && inner is JsonArray array ? array.OfType<JsonNode>() : [inner];
            }

            foreach (var item in items)
            {
                var rowVariables = new Dictionary<string, object?>(variables);
                try
                {
                    var release = ReadFields(
                        definition,
                        rowVariables,
                        pageUri,
                        context,
                        (selector, required) => JsonValue(selector, item, row, rowVariables, context, required));

                    if (Keep(definition, release, variables))
                    {
                        releases.Add(release);
                    }
                }
                catch (Exception exception) when (exception is FormatException or TrackerFieldException or NotSupportedException or InvalidOperationException or RegexMatchTimeoutException)
                {
                    skipped++;
                    if (problems.Count < 3)
                    {
                        problems.Add(exception.Message);
                    }
                }
            }
        }

        return new TrackerSearchResult(releases, problems, skipped);
    }

    private static string? JsonValue(
        TrackerSelector selector,
        JsonNode? item,
        JsonNode? parent,
        IReadOnlyDictionary<string, object?> variables,
        TrackerContext context,
        bool required)
    {
        if (selector.Text is not null)
        {
            return TrackerFilters.Apply(TrackerTemplate.Render(selector.Text, variables), selector.Filters, variables, context);
        }

        string? value = null;
        if (!string.IsNullOrEmpty(selector.Selector))
        {
            var fromParent = selector.Selector.StartsWith("..", StringComparison.Ordinal);
            var path = TrackerTemplate.Render(selector.Selector.TrimStart('.'), variables);
            var node = TrackerJson.Select(fromParent ? parent : item, path.Split(':')[0]);
            if (node is not null && path.Contains(':', StringComparison.Ordinal) && TrackerJson.Value(fromParent ? parent : item, path) is null)
            {
                node = null;
            }

            if (node is null)
            {
                return required ? throw new TrackerFieldException($"The JSON has no \"{path}\".") : null;
            }

            value = node is JsonArray array ? string.Join(",", array.Select(TrackerJson.Text)) : TrackerJson.Text(node);
        }

        if (selector.Case.Count > 0)
        {
            string? meaning = null;
            foreach (var (test, result) in selector.Case)
            {
                if (test == "*" || string.Equals(value, test, StringComparison.OrdinalIgnoreCase))
                {
                    meaning = result;
                    break;
                }
            }

            if (meaning is null)
            {
                return required ? throw new TrackerFieldException("No case matched.") : null;
            }

            value = meaning;
        }

        return value is null ? null : TrackerFilters.Apply(value, selector.Filters, variables, context);
    }

    // ---- fields ------------------------------------------------------------

    /// <summary>Fields Cardigann never lets cost a release: extras a site may leave out of one row.</summary>
    private static readonly HashSet<string> ImplicitlyOptional =
        ["imdb", "imdbid", "tmdbid", "rageid", "tvdbid", "tvmazeid", "traktid", "doubanid", "poster", "banner", "description", "genre"];

    /// <summary>
    /// Every field in order. Each value is kept as <c>.Result.name</c> for the
    /// fields after it; the named ones also land on the release.
    /// </summary>
    private static TrackerRelease ReadFields(
        TrackerDefinition definition,
        Dictionary<string, object?> variables,
        Uri pageUri,
        TrackerContext context,
        Func<TrackerSelector, bool, string?> read)
    {
        var release = new TrackerRelease();
        foreach (var (key, selector) in definition.Search.Fields)
        {
            var parts = key.Split('|');
            var name = parts[0];
            var modifiers = parts.Skip(1).ToArray();
            var optional = selector.Optional || modifiers.Contains("optional") || ImplicitlyOptional.Contains(name);
            var variable = ".Result." + name;

            string? value;
            try
            {
                value = read(selector, !optional && selector.Default is null);
            }
            catch (Exception) when (optional)
            {
                value = null;
            }

            if (string.IsNullOrWhiteSpace(value) && selector.Default is not null)
            {
                value = TrackerFilters.Apply(TrackerTemplate.Render(selector.Default, variables), selector.Filters, variables, context);
            }

            if (optional && string.IsNullOrWhiteSpace(value))
            {
                variables[variable] = null;
                continue;
            }

            variables[variable] = Assign(definition, release, name, value ?? string.Empty, modifiers, pageUri, context);
        }

        return release;
    }

    /// <summary>Puts one named field on the release, and returns the value later fields see.</summary>
    private static string? Assign(
        TrackerDefinition definition,
        TrackerRelease release,
        string name,
        string value,
        string[] modifiers,
        Uri pageUri,
        TrackerContext context)
    {
        var append = modifiers.Contains("append");
        switch (name)
        {
            case "title":
                release.Title = append ? release.Title + value : value;
                return value;

            case "description":
                release.Description = append ? release.Description + value : value;
                return value;

            case "download":
                if (value.Length == 0)
                {
                    return null;
                }

                if (value.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                {
                    release.MagnetUrl = value;
                    return value;
                }

                release.DownloadUrl = Resolve(pageUri, value);
                return release.DownloadUrl;

            case "magnet":
                release.MagnetUrl = value.Length == 0 ? null : value;
                return release.MagnetUrl;

            case "infohash":
                release.InfoHash = value.Length == 0 ? null : value.Trim();
                return release.InfoHash;

            case "details":
            case "comments":
                release.DetailsUrl = value.Length == 0 ? null : Resolve(pageUri, value);
                return release.DetailsUrl;

            case "poster":
                release.PosterUrl = value.Length == 0 ? null : Resolve(pageUri, value);
                return release.PosterUrl;

            case "category":
            case "categorydesc":
                foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    foreach (var number in TrackerCategories.NumbersFor(definition, part, byDescription: name == "categorydesc"))
                    {
                        if (!release.Categories.Contains(number))
                        {
                            release.Categories.Add(number);
                        }
                    }
                }

                return value;

            case "size":
                release.Size = TrackerNumbers.ParseSize(value);
                return release.Size?.ToString(CultureInfo.InvariantCulture);

            case "seeders":
            {
                var seeders = TrackerNumbers.ParseInt(value) is { } parsed and < 5_000_000 ? parsed : 0;
                release.Seeders = seeders;
                release.Peers = (release.Peers ?? 0) + seeders;
                return seeders.ToString(CultureInfo.InvariantCulture);
            }

            case "leechers":
            {
                var leechers = TrackerNumbers.ParseInt(value) is { } parsed and < 5_000_000 ? parsed : 0;
                release.Peers = (release.Peers ?? 0) + leechers;
                return leechers.ToString(CultureInfo.InvariantCulture);
            }

            case "grabs":
                release.Grabs = TrackerNumbers.ParseInt(value);
                return release.Grabs?.ToString(CultureInfo.InvariantCulture);

            case "files":
                release.Files = TrackerNumbers.ParseInt(value);
                return release.Files?.ToString(CultureInfo.InvariantCulture);

            case "date":
                // A date Deluno cannot read costs the date, not the release.
                try
                {
                    release.PublishDate = TrackerDates.FromUnknown(value, context.Now);
                    return TrackerDates.Format(release.PublishDate.Value);
                }
                catch (FormatException)
                {
                    return value;
                }

            case "downloadvolumefactor":
                release.DownloadVolumeFactor = TrackerNumbers.ParseDouble(value);
                return value;

            case "uploadvolumefactor":
                release.UploadVolumeFactor = TrackerNumbers.ParseDouble(value);
                return value;

            case "minimumratio":
                release.MinimumRatio = TrackerNumbers.ParseDouble(value);
                return value;

            case "minimumseedtime":
                release.MinimumSeedTimeSeconds = (long?)TrackerNumbers.ParseDouble(value);
                return value;

            case "imdb":
            case "imdbid":
                release.ImdbId = TrackerNumbers.ParseImdbId(value);
                return release.ImdbId;

            case "tmdbid":
                release.TmdbId = FirstNumber(value);
                return release.TmdbId?.ToString(CultureInfo.InvariantCulture);

            case "tvdbid":
                release.TvdbId = FirstNumber(value);
                return release.TvdbId?.ToString(CultureInfo.InvariantCulture);

            case "year":
                release.Year = TrackerNumbers.ParseInt(value);
                return value;

            case "genre":
                release.Genre = value;
                return value;

            default:
                return value;
        }
    }

    /// <summary>Row filters, then the minimum a release must have to be of any use.</summary>
    private static bool Keep(TrackerDefinition definition, TrackerRelease release, IReadOnlyDictionary<string, object?> variables)
    {
        if (string.IsNullOrWhiteSpace(release.Title))
        {
            return false;
        }

        if (release.MagnetUrl is null && release.DownloadUrl is null && release.InfoHash is { Length: > 0 } hash)
        {
            release.MagnetUrl = TrackerMagnet.Build(hash, release.Title);
        }

        if (release.Link is null)
        {
            return false;
        }

        foreach (var filter in definition.Search.Rows.Filters)
        {
            if (filter.Name == "andmatch"
                && variables.GetValueOrDefault(".Query.IMDBID") is null
                && !TrackerFilters.MatchesAllWords(
                    release.Title,
                    TrackerTemplate.AsString(variables.GetValueOrDefault(".Keywords")),
                    filter.Args.Count > 0 && int.TryParse(filter.Args[0], out var limit) ? limit : -1))
            {
                return false;
            }
        }

        return true;
    }

    private static int? FirstNumber(string value)
    {
        var match = Digits().Match(value);
        return match.Success && int.TryParse(match.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;
    }

    public static string Resolve(Uri pageUri, string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            trimmed = pageUri.Scheme + ":" + trimmed;
        }

        return Uri.TryCreate(pageUri, trimmed, out var resolved) ? resolved.AbsoluteUri : trimmed;
    }

    public static string NormalizeSpace(string value) => Whitespace().Replace(value, " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();
}

/// <summary>A field a row needed and did not have.</summary>
public sealed class TrackerFieldException(string message) : Exception(message);

/// <summary>What the tracker itself said went wrong, in its words.</summary>
public sealed class TrackerSiteException(string message) : Exception(message);

public static class TrackerMagnet
{
    private static readonly string[] Announce =
    [
        "udp://tracker.opentrackr.org:1337/announce",
        "udp://open.demonii.com:1337/announce",
        "udp://tracker.torrent.eu.org:451/announce",
        "udp://exodus.desync.com:6969/announce",
        "udp://open.stealth.si:80/announce"
    ];

    public static string Build(string infoHash, string? title)
    {
        var magnet = "magnet:?xt=urn:btih:" + infoHash.Trim();
        if (!string.IsNullOrWhiteSpace(title))
        {
            magnet += "&dn=" + Uri.EscapeDataString(title);
        }

        return magnet + string.Concat(Announce.Select(tracker => "&tr=" + Uri.EscapeDataString(tracker)));
    }

    /// <summary>The info hash a magnet names, upper-case hex or base32 as written.</summary>
    public static string? InfoHashOf(string? magnet)
    {
        if (magnet is null)
        {
            return null;
        }

        var match = Regex.Match(magnet, @"urn:btih:([A-Za-z0-9]{32,40})", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
    }
}
