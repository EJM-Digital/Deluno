namespace Deluno.Integrations.Search.Trackers;

/// <summary>
/// Whether the engine can run a definition (#606), checked once when it loads
/// rather than discovered on a search: every template must parse, every filter
/// must be one the engine has, and the login must be one Deluno can do alone.
/// </summary>
public static class TrackerDefinitionValidator
{
    private static readonly HashSet<string> KnownFilters = new(StringComparer.OrdinalIgnoreCase)
    {
        "querystring", "dateparse", "timeparse", "regexp", "re_replace", "split", "replace", "trim", "prepend", "append",
        "tolower", "toupper", "urldecode", "urlencode", "htmldecode", "htmlencode", "timeago", "reltime", "fuzzytime",
        "validfilename", "diacritics", "validate", "jsonjoinarray", "hexdump", "strdump"
    };

    private static readonly HashSet<string> KnownRowFilters = new(StringComparer.OrdinalIgnoreCase) { "andmatch", "strdump" };

    private static readonly HashSet<string> KnownLogins = new(StringComparer.OrdinalIgnoreCase) { "post", "form", "cookie", "get", "oneurl" };

    /// <summary>Why the engine cannot run this definition, or null when it can.</summary>
    public static string? Unsupported(TrackerDefinition definition)
    {
        if (definition.Search.Paths.Count == 0)
        {
            return "It has no search address.";
        }

        if (definition.Login is { } login)
        {
            if (login.Method is { } method && !KnownLogins.Contains(method))
            {
                return $"It signs in with '{method}', which Deluno does not do.";
            }

            if (login.HasCaptcha)
            {
                return "Signing in needs a captcha solved by hand.";
            }
        }

        try
        {
            foreach (var template in Templates(definition))
            {
                TrackerTemplate.Validate(template);
            }
        }
        catch (FormatException exception)
        {
            return exception.Message;
        }

        var unknownFilter = Filters(definition).FirstOrDefault(filter => !KnownFilters.Contains(filter.Name));
        if (unknownFilter is not null)
        {
            return $"It uses the filter '{unknownFilter.Name}', which Deluno does not have.";
        }

        var unknownRowFilter = definition.Search.Rows.Filters.FirstOrDefault(filter => !KnownRowFilters.Contains(filter.Name));
        return unknownRowFilter is null ? null : $"It uses the row filter '{unknownRowFilter.Name}', which Deluno does not have.";
    }

    private static IEnumerable<string?> Templates(TrackerDefinition definition)
    {
        var search = definition.Search;
        foreach (var path in search.Paths)
        {
            yield return path.Path;
            yield return path.Method;
            foreach (var input in path.Inputs)
            {
                yield return input.Value;
            }
        }

        foreach (var input in search.Inputs)
        {
            yield return input.Value;
        }

        foreach (var header in search.Headers.SelectMany(header => header.Value))
        {
            yield return header;
        }

        yield return search.Rows.Selector;
        foreach (var selector in Selectors(definition))
        {
            yield return selector.Selector;
            yield return selector.Text;
            yield return selector.Default;
        }

        foreach (var filter in Filters(definition))
        {
            foreach (var arg in filter.Args)
            {
                yield return arg;
            }
        }

        if (definition.Login is { } login)
        {
            yield return login.Path;
            yield return login.SubmitPath;
            foreach (var input in login.Inputs)
            {
                yield return input.Value;
            }
        }

        if (definition.Download?.Before is { } before)
        {
            yield return before.Path;
            foreach (var input in before.Inputs)
            {
                yield return input.Value;
            }
        }
    }

    private static IEnumerable<TrackerSelector> Selectors(TrackerDefinition definition)
    {
        foreach (var field in definition.Search.Fields)
        {
            yield return field.Value;
        }

        if (definition.Search.Rows.DateHeaders is { } dateHeaders)
        {
            yield return dateHeaders;
        }

        if (definition.Search.Rows.Count is { } count)
        {
            yield return count;
        }

        if (definition.Download is { } download)
        {
            foreach (var selector in download.Selectors)
            {
                yield return selector;
            }

            if (download.InfoHash is { } hash)
            {
                yield return hash;
            }

            if (download.InfoHashTitle is { } title)
            {
                yield return title;
            }

            if (download.Before?.PathSelector is { } pathSelector)
            {
                yield return pathSelector;
            }
        }

        if (definition.Login is { } login)
        {
            foreach (var input in login.SelectorInputs.Concat(login.GetSelectorInputs))
            {
                yield return input.Value;
            }

            foreach (var error in login.Errors)
            {
                if (error.Message is not null)
                {
                    yield return error.Message;
                }
            }
        }
    }

    private static IEnumerable<TrackerFilter> Filters(TrackerDefinition definition)
        => Selectors(definition).SelectMany(selector => selector.Filters)
            .Concat(definition.Search.KeywordsFilters)
            .Concat(definition.Search.PreprocessingFilters);
}
