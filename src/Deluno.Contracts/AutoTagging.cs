using System.Globalization;

namespace Deluno.Contracts;

/// <summary>
/// Auto-tagging (#1024): a rule is some tags plus some conditions, and a title
/// that meets the conditions is given the tags. Checked against Sonarr's and
/// Radarr's own AutoTaggingService: conditions of the same kind are OR-ed,
/// conditions of different kinds are AND-ed, each can be negated, and a rule
/// set to "remove tags automatically" takes its tags back off a title that
/// stops matching.
/// </summary>
public static class AutoTagSpecificationTypes
{
    // Sonarr and Radarr's own conditions.
    public const string Genre = "genre";
    public const string Monitored = "monitored";
    public const string OriginalLanguage = "originalLanguage";
    public const string QualityProfile = "qualityProfile";
    public const string RootFolder = "rootFolder";
    public const string SeriesType = "seriesType";
    public const string Status = "status";
    public const string Tag = "tag";
    public const string Year = "year";
    public const string Keyword = "keyword";
    public const string Runtime = "runtime";
    public const string Studio = "studio";

    // What only Deluno knows.
    public const string Library = "library";
    public const string Anime = "anime";
    public const string Certification = "certification";
    public const string Collection = "collection";
    public const string Has4kFile = "has4kFile";
    public const string Requested4k = "requested4k";
    public const string ViaSeerr = "viaSeerr";
    public const string SourceList = "sourceList";

    public static readonly IReadOnlyList<string> All =
    [
        Genre, Monitored, OriginalLanguage, QualityProfile, RootFolder, SeriesType, Status, Tag, Year, Keyword, Runtime, Studio,
        Library, Anime, Certification, Collection, Has4kFile, Requested4k, ViaSeerr, SourceList
    ];

    /// <summary>Only known while a title is being added; a later edit or refresh cannot tell, so it leaves such rules alone.</summary>
    public static bool IsAddTimeOnly(string type) => type is Requested4k or ViaSeerr or SourceList;

    /// <summary>Conditions written as a range of numbers ("2000-2010", "-1999", "2015-").</summary>
    public static bool IsRange(string type) => type is Year or Runtime;

    /// <summary>Conditions that are simply yes or no.</summary>
    public static bool IsYesNo(string type) => type is Monitored or Anime or Has4kFile or Requested4k or ViaSeerr;

    /// <summary>Conditions that only make sense for one kind of title.</summary>
    public static bool AppliesTo(string type, MediaKind kind) => type switch
    {
        SeriesType => kind == MediaKind.Series,
        _ => true
    };
}

public static class AutoTagScopes
{
    public const string Both = "both";
    public const string Movies = "movies";
    public const string Series = "series";

    public static string Normalize(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        Movies => Movies,
        Series or "tv" => Series,
        _ => Both
    };

    public static bool Covers(string scope, MediaKind kind) => scope switch
    {
        Movies => kind == MediaKind.Movie,
        Series => kind == MediaKind.Series,
        _ => true
    };
}

public sealed record AutoTagSpecification(string Type, bool Negate, string Value, string? Id = null);

public sealed record AutoTagRule(
    string Id,
    string Name,
    IReadOnlyList<string> Tags,
    bool RemoveAutomatically,
    string AppliesTo,
    bool IsEnabled,
    IReadOnlyList<AutoTagSpecification> Specifications,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

public sealed record SaveAutoTagRuleRequest(
    string? Name,
    IReadOnlyList<string>? Tags,
    bool? RemoveAutomatically,
    string? AppliesTo,
    bool? IsEnabled,
    IReadOnlyList<AutoTagSpecification>? Specifications);

/// <summary>What is known about a title when the rules are checked against it.</summary>
public sealed record AutoTagFacts(
    MediaKind Kind,
    string Title,
    IReadOnlyList<string> Genres,
    bool Monitored,
    string? OriginalLanguage,
    string? QualityProfileId,
    IReadOnlyList<string> LibraryIds,
    IReadOnlyList<string> RootFolders,
    string? SeriesType,
    string? Status,
    IReadOnlyList<string> Tags,
    int? Year,
    IReadOnlyList<string> Keywords,
    int? RuntimeMinutes,
    string? Studio,
    string? Certification,
    bool IsAnime,
    string? Collection,
    bool Has4kFile,
    // The conditions only an add can answer: null when this is an edit or a refresh.
    AutoTagAddFacts? Add = null);

/// <summary>What a title's add knows that an edit or refresh cannot (#1024).</summary>
public sealed record AutoTagAddFacts(bool Requested4k, bool ViaSeerr, string? SourceList);

public enum AutoTagTrigger
{
    Added,
    Edited,
    Refreshed,
    RunNow
}

public enum AutoTagVerdict
{
    NoMatch,
    Match,
    /// <summary>The rule asks something only an add can answer, and this is not an add.</summary>
    NotDecidable
}

public sealed record AutoTagChange(string Tag, string RuleName);

public sealed record AutoTagChanges(IReadOnlyList<AutoTagChange> Add, IReadOnlyList<AutoTagChange> Remove)
{
    public static readonly AutoTagChanges None = new([], []);
    public bool IsEmpty => Add.Count == 0 && Remove.Count == 0;
}

public static class AutoTagMatcher
{
    public static AutoTagVerdict Evaluate(AutoTagRule rule, AutoTagFacts facts)
    {
        var specifications = rule.Specifications
            .Where(spec => AutoTagSpecificationTypes.AppliesTo(spec.Type, facts.Kind))
            .ToList();
        if (specifications.Count == 0)
        {
            return AutoTagVerdict.NoMatch;
        }

        if (facts.Add is null && specifications.Any(spec => AutoTagSpecificationTypes.IsAddTimeOnly(spec.Type)))
        {
            return AutoTagVerdict.NotDecidable;
        }

        foreach (var group in specifications.GroupBy(spec => spec.Type, StringComparer.OrdinalIgnoreCase))
        {
            // Conditions of one kind are alternatives; the rule needs at least one of them.
            if (!group.Any(spec => IsSatisfied(spec, facts)))
            {
                return AutoTagVerdict.NoMatch;
            }
        }

        return AutoTagVerdict.Match;
    }

    /// <summary>
    /// What the rules do to a title carrying <paramref name="facts"/>: the tags
    /// to add, and, for rules that remove automatically, the tags to take back.
    /// A tag another matching rule wants stays.
    /// </summary>
    public static AutoTagChanges Changes(IEnumerable<AutoTagRule> rules, AutoTagFacts facts)
    {
        var held = facts.Tags.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var add = new List<AutoTagChange>();
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<(string Tag, string Rule)>();

        foreach (var rule in rules.Where(rule => rule.IsEnabled && AutoTagScopes.Covers(rule.AppliesTo, facts.Kind)))
        {
            switch (Evaluate(rule, facts))
            {
                case AutoTagVerdict.Match:
                    foreach (var tag in rule.Tags)
                    {
                        wanted.Add(tag);
                        if (!held.Contains(tag) && !add.Any(item => string.Equals(item.Tag, tag, StringComparison.OrdinalIgnoreCase)))
                        {
                            add.Add(new AutoTagChange(tag, rule.Name));
                        }
                    }

                    break;
                case AutoTagVerdict.NoMatch when rule.RemoveAutomatically:
                    foreach (var tag in rule.Tags)
                    {
                        candidates.Add((tag, rule.Name));
                    }

                    break;
            }
        }

        var remove = new List<AutoTagChange>();
        foreach (var (tag, ruleName) in candidates)
        {
            if (held.Contains(tag) && !wanted.Contains(tag) && !remove.Any(item => string.Equals(item.Tag, tag, StringComparison.OrdinalIgnoreCase)))
            {
                remove.Add(new AutoTagChange(tag, ruleName));
            }
        }

        return new AutoTagChanges(add, remove);
    }

    private static bool IsSatisfied(AutoTagSpecification spec, AutoTagFacts facts)
    {
        var matched = IsSatisfiedWithoutNegate(spec, facts);
        return spec.Negate ? !matched : matched;
    }

    private static bool IsSatisfiedWithoutNegate(AutoTagSpecification spec, AutoTagFacts facts)
    {
        var value = spec.Value?.Trim() ?? string.Empty;
        switch (spec.Type)
        {
            case AutoTagSpecificationTypes.Genre:
                return facts.Genres.Any(genre => Same(genre, value));
            case AutoTagSpecificationTypes.Monitored:
                return YesNo(value) == facts.Monitored;
            case AutoTagSpecificationTypes.OriginalLanguage:
                return AutoTagLanguages.Same(facts.OriginalLanguage, value);
            case AutoTagSpecificationTypes.QualityProfile:
                return !string.IsNullOrWhiteSpace(facts.QualityProfileId) && Same(facts.QualityProfileId, value);
            case AutoTagSpecificationTypes.Library:
                return facts.LibraryIds.Any(id => Same(id, value));
            case AutoTagSpecificationTypes.RootFolder:
                return facts.RootFolders.Any(folder => SameFolder(folder, value));
            case AutoTagSpecificationTypes.SeriesType:
                return Same(facts.SeriesType, value);
            case AutoTagSpecificationTypes.Status:
                return Same(facts.Status, value);
            case AutoTagSpecificationTypes.Tag:
                return facts.Tags.Any(tag => Same(tag, value));
            case AutoTagSpecificationTypes.Year:
                return InRange(facts.Year, value);
            case AutoTagSpecificationTypes.Keyword:
                return facts.Keywords.Any(keyword => Same(keyword, value));
            case AutoTagSpecificationTypes.Runtime:
                return InRange(facts.RuntimeMinutes, value);
            case AutoTagSpecificationTypes.Studio:
                return Same(facts.Studio, value);
            case AutoTagSpecificationTypes.Certification:
                return Same(facts.Certification, value);
            case AutoTagSpecificationTypes.Anime:
                return YesNo(value) == facts.IsAnime;
            case AutoTagSpecificationTypes.Collection:
                return Same(facts.Collection, value);
            case AutoTagSpecificationTypes.Has4kFile:
                return YesNo(value) == facts.Has4kFile;
            case AutoTagSpecificationTypes.Requested4k:
                return facts.Add is not null && YesNo(value) == facts.Add.Requested4k;
            case AutoTagSpecificationTypes.ViaSeerr:
                return facts.Add is not null && YesNo(value) == facts.Add.ViaSeerr;
            case AutoTagSpecificationTypes.SourceList:
                return facts.Add is not null && Same(facts.Add.SourceList, value);
            default:
                return false;
        }
    }

    private static bool Same(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) &&
        !string.IsNullOrWhiteSpace(right) &&
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool YesNo(string value) =>
        !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(value, "no", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeFolder(string? path) =>
        (path ?? string.Empty).Trim().Replace('\\', '/').TrimEnd('/').ToLowerInvariant();

    private static bool SameFolder(string? folder, string wanted)
    {
        var left = NormalizeFolder(folder);
        var right = NormalizeFolder(wanted);
        return left.Length > 0 && left == right;
    }

    private static bool InRange(int? actual, string range)
    {
        if (actual is null || !TryParseRange(range, out var from, out var to))
        {
            return false;
        }

        return (from is null || actual >= from) && (to is null || actual <= to);
    }

    /// <summary>"2000-2010", "-1999", "2015-" or a single number.</summary>
    public static bool TryParseRange(string? text, out int? from, out int? to)
    {
        from = null;
        to = null;
        var value = (text ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return false;
        }

        var dash = value.IndexOf('-', StringComparison.Ordinal);
        if (dash < 0)
        {
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var single))
            {
                return false;
            }

            from = to = single;
            return true;
        }

        var left = value[..dash].Trim();
        var right = value[(dash + 1)..].Trim();
        if (left.Length == 0 && right.Length == 0)
        {
            return false;
        }

        if (left.Length > 0)
        {
            if (!int.TryParse(left, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                return false;
            }

            from = parsed;
        }

        if (right.Length > 0)
        {
            if (!int.TryParse(right, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                return false;
            }

            to = parsed;
        }

        return from is null || to is null || from <= to;
    }
}

/// <summary>Language codes and names compare as one thing: "ja" is "Japanese".</summary>
public static class AutoTagLanguages
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["english"] = "en", ["japanese"] = "ja", ["korean"] = "ko", ["french"] = "fr", ["german"] = "de",
        ["spanish"] = "es", ["italian"] = "it", ["chinese"] = "zh", ["mandarin"] = "zh", ["portuguese"] = "pt",
        ["russian"] = "ru", ["hindi"] = "hi", ["dutch"] = "nl", ["swedish"] = "sv", ["danish"] = "da",
        ["norwegian"] = "no", ["finnish"] = "fi", ["turkish"] = "tr", ["polish"] = "pl", ["thai"] = "th"
    };

    public static string Normalize(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        return Names.TryGetValue(text, out var code) ? code : text.ToLowerInvariant();
    }

    public static bool Same(string? left, string? right)
    {
        var a = Normalize(left);
        return a.Length > 0 && a == Normalize(right);
    }
}

public static class AutoTagRuleValidator
{
    /// <summary>Plain-words problems with a rule as it was submitted; empty when it can be saved.</summary>
    public static IReadOnlyDictionary<string, string[]> Validate(SaveAutoTagRuleRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            errors["name"] = ["Give the rule a name."];
        }

        if ((request.Tags ?? []).All(tag => string.IsNullOrWhiteSpace(tag)))
        {
            errors["tags"] = ["Choose at least one tag for the rule to give."];
        }

        var specs = request.Specifications ?? [];
        if (specs.Count == 0)
        {
            errors["specifications"] = ["Add at least one condition, or the rule would never match anything."];
            return errors;
        }

        foreach (var spec in specs)
        {
            if (!AutoTagSpecificationTypes.All.Contains(spec.Type, StringComparer.Ordinal))
            {
                errors["specifications"] = ["One of the conditions is not one Deluno knows."];
                return errors;
            }

            if (AutoTagSpecificationTypes.IsRange(spec.Type) && !AutoTagMatcher.TryParseRange(spec.Value, out _, out _))
            {
                errors["specifications"] = [$"\"{spec.Value}\" is not a range of numbers. Write it like 2000-2010, -1999 or 2015-."];
                return errors;
            }

            if (!AutoTagSpecificationTypes.IsYesNo(spec.Type) && string.IsNullOrWhiteSpace(spec.Value))
            {
                errors["specifications"] = ["Every condition needs a value."];
                return errors;
            }
        }

        return errors;
    }
}

/// <summary>Where the rules are kept (platform database).</summary>
public interface IAutoTagRuleStore
{
    Task<IReadOnlyList<AutoTagRule>> ListAsync(CancellationToken cancellationToken);

    Task<AutoTagRule?> GetAsync(string id, CancellationToken cancellationToken);

    Task<AutoTagRule> CreateAsync(SaveAutoTagRuleRequest request, CancellationToken cancellationToken);

    Task<AutoTagRule?> UpdateAsync(string id, SaveAutoTagRuleRequest request, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken);
}

/// <summary>What one pass of auto-tagging did to one title.</summary>
public sealed record AutoTagOutcome(IReadOnlyList<string> Added, IReadOnlyList<string> Removed)
{
    public static readonly AutoTagOutcome None = new([], []);
    public bool Changed => Added.Count > 0 || Removed.Count > 0;
}

/// <summary>
/// Applies the auto-tagging rules to titles (#1024). Every add path, edit and
/// metadata refresh calls this one service, so a rule behaves the same
/// wherever a title comes from. A pass that changes tags writes one Activity
/// line in plain words.
/// </summary>
public interface IAutoTaggingService
{
    /// <summary>
    /// Checks the rules against an existing title and gives or removes tags.
    /// <paramref name="add"/> is passed only when the title has just been added,
    /// and carries what only an add knows (a 4K request, Seerr, the list).
    /// <paramref name="libraryIds"/> names the libraries a just-added title is
    /// going into when its wanted state is not written yet.
    /// </summary>
    Task<AutoTagOutcome> ApplyAsync(
        MediaKind kind,
        string mediaId,
        AutoTagTrigger trigger,
        AutoTagAddFacts? add,
        IReadOnlyList<string>? libraryIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// The tags the rules would give a title that does not exist yet, for
    /// routing that has to decide before the title is added.
    /// </summary>
    Task<IReadOnlyList<string>> PredictAsync(AutoTagFacts facts, CancellationToken cancellationToken);

    /// <summary>Checks every title in one catalogue, or both when <paramref name="kind"/> is null. Returns how many titles changed.</summary>
    Task<int> ApplyToAllAsync(MediaKind? kind, CancellationToken cancellationToken);
}

public static class AutoTaggingServiceExtensions
{
    /// <summary>Runs the rules over titles that were just edited (#1024).</summary>
    public static async Task ApplyEditedAsync(
        this IAutoTaggingService service,
        MediaKind kind,
        IEnumerable<string> mediaIds,
        CancellationToken cancellationToken)
    {
        foreach (var id in mediaIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await service.ApplyAsync(kind, id, AutoTagTrigger.Edited, null, null, cancellationToken);
        }
    }
}

/// <summary>Builds the facts of a title that is not in the catalogue yet (#1024), for routing that decides first.</summary>
public static class AutoTagFactsBuilder
{
    public static bool LooksLikeAnime(string? seriesType, IReadOnlyList<string> genres, string? originalLanguage) =>
        string.Equals(seriesType, "anime", StringComparison.OrdinalIgnoreCase) ||
        genres.Any(genre => string.Equals(genre, "Anime", StringComparison.OrdinalIgnoreCase)) ||
        (genres.Any(genre => string.Equals(genre, "Animation", StringComparison.OrdinalIgnoreCase)) && AutoTagLanguages.Same(originalLanguage, "ja"));

    public static AutoTagFacts ForNewTitle(
        MediaKind kind,
        string title,
        int? year,
        IReadOnlyList<string> genres,
        string? originalLanguage,
        string? certification,
        string? studio,
        int? runtimeMinutes,
        IReadOnlyList<string> keywords,
        string? status,
        string? collection,
        IReadOnlyList<string> currentTags,
        AutoTagAddFacts add,
        bool monitored = true,
        string? seriesType = null) =>
        new(
            kind,
            title,
            genres,
            monitored,
            originalLanguage,
            null,
            [],
            [],
            seriesType,
            status,
            currentTags,
            year,
            keywords,
            runtimeMinutes,
            studio,
            certification,
            LooksLikeAnime(seriesType, genres, originalLanguage),
            collection,
            false,
            add);
}
