namespace Deluno.Integrations.Search.Trackers;

/// <summary>
/// The Newznab category numbers, by the names definitions map their own
/// categories to (#606), and the translation both ways between a tracker's
/// categories and the numbers Deluno's indexers are configured with.
/// </summary>
public static class TrackerCategories
{
    public static readonly IReadOnlyDictionary<string, int> Standard = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["Console"] = 1000, ["Console/NDS"] = 1010, ["Console/PSP"] = 1020, ["Console/Wii"] = 1030, ["Console/XBox"] = 1040,
        ["Console/XBox 360"] = 1050, ["Console/Wiiware"] = 1060, ["Console/XBox 360 DLC"] = 1070, ["Console/PS3"] = 1080,
        ["Console/Other"] = 1090, ["Console/3DS"] = 1110, ["Console/PS Vita"] = 1120, ["Console/WiiU"] = 1130,
        ["Console/XBox One"] = 1140, ["Console/PS4"] = 1180,
        ["Movies"] = 2000, ["Movies/Foreign"] = 2010, ["Movies/Other"] = 2020, ["Movies/SD"] = 2030, ["Movies/HD"] = 2040,
        ["Movies/UHD"] = 2045, ["Movies/BluRay"] = 2050, ["Movies/3D"] = 2060, ["Movies/DVD"] = 2070, ["Movies/WEB-DL"] = 2080,
        ["Movies/x265"] = 2090,
        ["Audio"] = 3000, ["Audio/MP3"] = 3010, ["Audio/Video"] = 3020, ["Audio/Audiobook"] = 3030, ["Audio/Lossless"] = 3040,
        ["Audio/Other"] = 3050, ["Audio/Foreign"] = 3060,
        ["PC"] = 4000, ["PC/0day"] = 4010, ["PC/ISO"] = 4020, ["PC/Mac"] = 4030, ["PC/Mobile-Other"] = 4040, ["PC/Games"] = 4050,
        ["PC/Mobile-iOS"] = 4060, ["PC/Mobile-Android"] = 4070,
        ["TV"] = 5000, ["TV/WEB-DL"] = 5010, ["TV/Foreign"] = 5020, ["TV/SD"] = 5030, ["TV/HD"] = 5040, ["TV/UHD"] = 5045,
        ["TV/Other"] = 5050, ["TV/Sport"] = 5060, ["TV/Anime"] = 5070, ["TV/Documentary"] = 5080, ["TV/x265"] = 5090,
        ["XXX"] = 6000, ["XXX/DVD"] = 6010, ["XXX/WMV"] = 6020, ["XXX/XviD"] = 6030, ["XXX/x264"] = 6040, ["XXX/UHD"] = 6045,
        ["XXX/Pack"] = 6050, ["XXX/ImageSet"] = 6060, ["XXX/Other"] = 6070, ["XXX/SD"] = 6080, ["XXX/WEB-DL"] = 6090,
        ["Books"] = 7000, ["Books/Mags"] = 7010, ["Books/EBook"] = 7020, ["Books/Comics"] = 7030, ["Books/Technical"] = 7040,
        ["Books/Other"] = 7050, ["Books/Foreign"] = 7060,
        ["Other"] = 8000, ["Other/Misc"] = 8010, ["Other/Hashed"] = 8020
    };

    public static int? NumberOf(string category) => Standard.TryGetValue(category.Trim(), out var number) ? number : null;

    /// <summary>
    /// The tracker's own categories that answer the Newznab numbers asked for:
    /// a parent (2000) takes in its children (2040), a child only itself.
    /// Nothing asked for is everything the tracker files under Movies or TV.
    /// </summary>
    public static IReadOnlyList<string> SiteCategoriesFor(TrackerDefinition definition, IReadOnlyCollection<int> requested)
    {
        var wanted = requested.Count > 0 ? requested : [2000, 5000];
        return definition.Caps.CategoryMappings
            .Where(mapping => NumberOf(mapping.Category) is { } number && wanted.Any(asked => Covers(asked, number)))
            .Select(mapping => mapping.SiteId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>The Newznab numbers a tracker's category id or description stands for.</summary>
    public static IReadOnlyList<int> NumbersFor(TrackerDefinition definition, string? siteCategory, bool byDescription = false)
    {
        if (string.IsNullOrWhiteSpace(siteCategory))
        {
            return [];
        }

        var value = siteCategory.Trim();
        return definition.Caps.CategoryMappings
            .Where(mapping => byDescription
                ? string.Equals(mapping.Description, value, StringComparison.OrdinalIgnoreCase)
                : string.Equals(mapping.SiteId, value, StringComparison.OrdinalIgnoreCase))
            .Select(mapping => NumberOf(mapping.Category))
            .OfType<int>()
            .Distinct()
            .ToArray();
    }

    public static bool Covers(int asked, int number)
        => asked == number || asked % 1000 == 0 && number / 1000 == asked / 1000;

    public static IReadOnlyList<int> Parse(string? categories)
        => (categories ?? string.Empty)
            .Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => int.TryParse(part, out var number) ? number : (int?)null)
            .OfType<int>()
            .ToArray();

    /// <summary>
    /// The categories a search asks one indexer for (#1337), through one rule for every search: only the
    /// family of the media being searched (2000 for a film, 5000 for TV). A person's ticked categories
    /// win, then the indexer's own configured list, and a list with nothing in the family asks the whole
    /// family. A film search never sends a TV category and a TV search never sends a film one.
    /// </summary>
    public static IReadOnlyList<int> ForFamily(bool isTv, IReadOnlyList<int>? requested, string? configured)
    {
        var family = isTv ? 5 : 2;
        var ticked = requested is { Count: > 0 } ? requested.Where(category => category / 1000 == family).ToArray() : [];
        if (ticked.Length > 0)
        {
            return ticked;
        }

        var own = Parse(configured).Where(category => category / 1000 == family).ToArray();
        return own.Length > 0 ? own : [family * 1000];
    }
}
