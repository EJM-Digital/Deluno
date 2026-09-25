using System.Globalization;

namespace Deluno.Integrations.Search.Trackers;

/// <summary>
/// The names a definition's templates read (#606), filled from the user's
/// settings and the search.
/// </summary>
public static class TrackerVariables
{
    public const string True = "True";

    public static Dictionary<string, object?> Build(TrackerConnection connection, TrackerQuery query, string siteLink)
    {
        var definition = connection.Definition;
        var variables = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [".Config.sitelink"] = siteLink,
            [".True"] = True,
            [".False"] = null,
            [".Today.Year"] = DateTime.UtcNow.Year.ToString(CultureInfo.InvariantCulture)
        };

        foreach (var setting in definition.Settings)
        {
            if (setting.IsInformational)
            {
                continue;
            }

            var given = connection.Settings.GetValueOrDefault(setting.Name);
            var value = string.IsNullOrEmpty(given) ? setting.Default : given;
            variables[".Config." + setting.Name] = setting.Type switch
            {
                "checkbox" => IsChecked(value) ? True : null,
                "select" => SelectedOption(setting, value),
                "multi-select" => (given is { Length: > 0 }
                        ? given.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        : setting.Defaults).ToList(),
                _ => value
            };
        }

        var mode = query.SearchType switch
        {
            "movie" => "movie-search",
            "tvsearch" => "tv-search",
            _ => "search"
        };

        // Only what the tracker says it can search by: a definition branches
        // on .Query.IMDBID, and a branch for a parameter the site does not
        // take would otherwise break the search.
        bool Supports(string parameter) => definition.Caps.Supports(mode, parameter);

        var imdb = Supports("imdbid") ? TrackerNumbers.ParseImdbId(query.ImdbId) : null;

        // A movie found by its IMDb id is searched by the id alone, the way
        // Radarr's own interactive search behaves: a definition may build
        // the id and the keywords into one path when both are set.
        var keywords = imdb is not null && mode == "movie-search" ? string.Empty : query.Keywords.Trim();
        variables[".Query.Type"] = query.SearchType;
        variables[".Query.Q"] = keywords;
        variables[".Query.Keywords"] = keywords;
        variables[".Query.Series"] = null;
        variables[".Query.Movie"] = null;
        variables[".Query.Season"] = Supports("season") ? query.Season?.ToString(CultureInfo.InvariantCulture) : null;
        variables[".Query.Ep"] = Supports("ep") ? query.Episode?.ToString(CultureInfo.InvariantCulture) : null;
        variables[".Query.Year"] = query.Year?.ToString(CultureInfo.InvariantCulture);
        variables[".Query.IMDBID"] = imdb;
        variables[".Query.IMDBIDShort"] = imdb?[2..];
        variables[".Query.TMDBID"] = Supports("tmdbid") ? query.TmdbId?.ToString(CultureInfo.InvariantCulture) : null;
        variables[".Query.TVDBID"] = Supports("tvdbid") ? query.TvdbId?.ToString(CultureInfo.InvariantCulture) : null;
        variables[".Query.DoubanID"] = null;
        variables[".Query.TVMazeID"] = null;
        variables[".Query.TraktID"] = null;
        variables[".Query.TVRageID"] = null;
        variables[".Query.Genre"] = null;
        variables[".Query.Limit"] = "100";
        variables[".Query.Offset"] = "0";
        variables[".Query.Extended"] = null;
        variables[".Query.APIKey"] = null;
        variables[".Query.Artist"] = null;
        variables[".Query.Album"] = null;
        variables[".Query.Author"] = null;
        variables[".Query.Title"] = null;
        variables[".Categories"] = TrackerCategories.SiteCategoriesFor(definition, query.Categories ?? []).ToList();

        var context = new TrackerContext(DateTimeOffset.UtcNow);
        variables[".Keywords"] = definition.Search.KeywordsFilters.Count == 0
            ? keywords
            : TrackerFilters.Apply(keywords, definition.Search.KeywordsFilters, variables, context);
        return variables;
    }

    private static bool IsChecked(string? value)
        => value is not null && (bool.TryParse(value, out var flag) ? flag : value is "1" or "on" or "yes");

    /// <summary>A select's value is an option key; a label typed instead is taken to mean its key.</summary>
    private static string? SelectedOption(TrackerSetting setting, string? value)
    {
        if (value is null || setting.Options.Count == 0 || setting.Options.Any(option => option.Key == value))
        {
            return value;
        }

        return setting.Options.FirstOrDefault(option => string.Equals(option.Value, value, StringComparison.OrdinalIgnoreCase)).Key ?? value;
    }
}
