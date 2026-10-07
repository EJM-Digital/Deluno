using System.Text.RegularExpressions;

namespace Deluno.Integrations.Search.Trackers;

/// <summary>
/// Deluno's words for a tracker's settings (#1137).
///
/// The bundled definitions are synced from upstream and written in the words of
/// the apps they were made for: "Improve Sonarr compatibility", raw sort values
/// such as <c>created</c> and <c>desc</c>, notes about another tool's dashboard.
/// Deluno replaces those apps, so none of that means anything to its user. The
/// definition files stay as they arrive (a sync would overwrite an edit); what
/// the screen shows is reworded here, and a setting that cannot be reworded is
/// not shown. The matching test fails if any shown text still names one of them.
/// </summary>
public static partial class TrackerSettingWording
{
    /// <summary>The switches that only made the site's results readable to those apps. Deluno turns them on itself.</summary>
    public static readonly IReadOnlySet<string> AlwaysOn = new HashSet<string>(StringComparer.Ordinal)
    {
        "sonarr_compatibility",
        "radarr_compatibility"
    };

    /// <summary>True when the text names an app Deluno replaces.</summary>
    public static bool NamesAnotherApp(string? text)
        => !string.IsNullOrEmpty(text) && OtherApps().IsMatch(text);

    /// <summary>The setting as Deluno shows it, or null when it is not shown.</summary>
    public static TrackerSettingSummary? Reword(TrackerSettingSummary setting)
    {
        if (AlwaysOn.Contains(setting.Name))
        {
            return null;
        }

        if (setting.Type == "info")
        {
            var text = Note(setting.Default);
            return string.IsNullOrWhiteSpace(text) ? null : setting with { Default = text, Label = Plain(setting.Label) };
        }

        var label = Label(setting.Name, setting.Label);
        if (NamesAnotherApp(label))
        {
            // A switch about another tool means nothing here and is left out. A box
            // the person has to fill in (a key, a name) stays, in plain words.
            if (setting.Type == "checkbox")
            {
                return null;
            }

            label = Regex.Replace(OtherApps().Replace(label, "Deluno"), "(Deluno)( (and|or|&) Deluno)+", "Deluno");
        }

        var options = setting.Options
            .Select(option => new KeyValuePair<string, string>(option.Key, OptionLabel(option.Value)))
            .ToArray();
        if (options.Any(option => NamesAnotherApp(option.Value)))
        {
            return null;
        }

        return setting with { Label = label, Options = options };
    }

    private static string Label(string name, string label)
    {
        var language = LanguageTag().Match(label);
        if (language.Success)
        {
            var tag = language.Groups["tag"].Value.ToUpperInvariant();
            var words = tag switch { "RUS" or "RUSSIAN" => "Russian", "UKR" => "Ukrainian", _ => tag };
            return $"Mark every result as {words} (adds {tag} to its title). Turn on only if this site is all in {words}; anything else is misread.";
        }

        if (label.StartsWith("Disable IMDB and TVDB ID search", StringComparison.OrdinalIgnoreCase))
        {
            return "Search by TMDB id only, not by IMDb or TVDB id. Try this if searching by id finds nothing here.";
        }

        return label.Trim() switch
        {
            "Sort requested from site" => "Sort results by",
            "Order requested from site" => "Order of results",
            var other => other
        };
    }

    private static string OptionLabel(string label) => label.Trim().ToLowerInvariant() switch
    {
        "created" or "added" or "date" or "time" => "Date added",
        "seeders" or "seeds" => "Most seeders",
        "leechers" or "peers" => "Most leechers",
        "size" => "Size",
        "name" or "title" => "Name",
        "desc" or "descending" => "Highest or newest first",
        "asc" or "ascending" => "Lowest or oldest first",
        _ => label
    };

    /// <summary>A note, with another tool's name cut out, or a Deluno wording for the ones that only made sense there.</summary>
    private static string? Note(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var text = Plain(html);
        if (text.StartsWith("As the iTorrents .torrent download link", StringComparison.OrdinalIgnoreCase))
        {
            return "This site's .torrent link fails from time to time, so Deluno uses the magnet link as a fallback. The BTCache and Torrage mirrors are not supported, because they need a captcha or a button press.";
        }

        text = OtherAppClause().Replace(text, string.Empty);
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return NamesAnotherApp(text) ? null : text;
    }

    private static string Plain(string text) => Regex.Replace(Regex.Replace(text, @"<br\s*/?>", " ", RegexOptions.IgnoreCase), "<[^>]+>", string.Empty).Replace("&amp;", "&").Trim();

    [GeneratedRegex(@"\b(Sonarr|Radarr|Lidarr|Readarr|Whisparr|Prowlarr|Jackett)\b|\*Arr\b", RegexOptions.IgnoreCase)]
    private static partial Regex OtherApps();

    // "(On the Jackett dashboard search results these are tagged as Freeleech)."
    [GeneratedRegex(@"\(\s*On the (Jackett|Prowlarr)[^)]*\)\.?", RegexOptions.IgnoreCase)]
    private static partial Regex OtherAppClause();

    [GeneratedRegex(@"^Add (?<tag>RUS|RUSSIAN|UKR) to end of all titles", RegexOptions.IgnoreCase)]
    private static partial Regex LanguageTag();
}
