namespace Deluno.Contracts;

/// <summary>
/// What a strike rule does to a torrent from a private tracker, or one a person has grouped with a seeding tag
/// (#1240). The default is <see cref="Remove"/>, what every rule has always done, so a rule saved before this
/// existed reads exactly as it did.
/// </summary>
public static class DownloadHealthPrivateActions
{
    /// <summary>Act as the rule's action says (for Replace: remove it, delete its files). The default.</summary>
    public const string Remove = "remove";

    /// <summary>
    /// Move the torrent to the keep-seeding category, leave it running, stop tracking it and look for another copy.
    /// Never deletes anything.
    /// </summary>
    public const string MoveAndReplace = "move-and-replace";

    public static readonly IReadOnlyList<string> All = [Remove, MoveAndReplace];

    public static bool IsKnown(string value) => All.Contains(value, StringComparer.OrdinalIgnoreCase);

    public static bool Moves(string? value) => string.Equals(value, MoveAndReplace, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// What a person said about one site (#1240): whether it is private when the site's own definition says otherwise
/// (or says nothing), the seeding tags it carries, and the category a torrent from it is moved to.
/// </summary>
/// <param name="PrivateOverride">Null follows the site's definition. True or false is the person's own answer, either way.</param>
/// <param name="Tags">
/// Seeding tags: names the person groups sites under. They only choose a keep-seeding category. They are not the
/// routing tags on an indexer, which decide what a site is searched for.
/// </param>
/// <param name="Category">The category a struck torrent from this site is moved to. Beats the client's, loses to a tag's.</param>
public sealed record KeepSeedingIndexerSettings(
    bool? PrivateOverride = null,
    IReadOnlyList<string>? Tags = null,
    string? Category = null)
{
    public IReadOnlyList<string> TagList => Tags ?? [];

    public bool IsEmpty => PrivateOverride is null && TagList.Count == 0 && string.IsNullOrWhiteSpace(Category);
}

/// <summary>
/// Everything the person has chosen for keeping a struck private torrent seeding (#1240). Empty is the state of a new
/// install and of every upgraded one: nothing is private, nothing is tagged, no category is set, and no rule moves.
/// </summary>
public sealed record KeepSeedingSettings(
    IReadOnlyDictionary<string, string>? ClientCategories = null,
    IReadOnlyDictionary<string, KeepSeedingIndexerSettings>? Indexers = null,
    IReadOnlyDictionary<string, string>? TagCategories = null)
{
    public static KeepSeedingSettings Empty { get; } = new();

    public IReadOnlyDictionary<string, string> ClientCategoryMap => ClientCategories ?? new Dictionary<string, string>();

    public IReadOnlyDictionary<string, KeepSeedingIndexerSettings> IndexerMap => Indexers ?? new Dictionary<string, KeepSeedingIndexerSettings>();

    public IReadOnlyDictionary<string, string> TagCategoryMap => TagCategories ?? new Dictionary<string, string>();

    public string? ClientCategory(string clientId)
        => ClientCategoryMap.FirstOrDefault(pair => string.Equals(pair.Key, clientId, StringComparison.OrdinalIgnoreCase)).Value is { Length: > 0 } value
            ? value
            : null;

    public KeepSeedingIndexerSettings? Indexer(string? indexerId)
        => string.IsNullOrWhiteSpace(indexerId)
            ? null
            : IndexerMap.FirstOrDefault(pair => string.Equals(pair.Key, indexerId, StringComparison.OrdinalIgnoreCase)).Value;

    public string? TagCategory(string tag)
        => TagCategoryMap.FirstOrDefault(pair => string.Equals(pair.Key, tag, StringComparison.OrdinalIgnoreCase)).Value is { Length: > 0 } value
            ? value
            : null;

    /// <summary>Trimmed, with blanks and repeats removed, so the store holds one spelling of each.</summary>
    public KeepSeedingSettings Normalized()
    {
        static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        var clients = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in ClientCategoryMap)
        {
            if (Clean(key) is { } id && Clean(value) is { } category) clients[id] = category;
        }

        var indexers = new Dictionary<string, KeepSeedingIndexerSettings>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in IndexerMap)
        {
            if (Clean(key) is not { } id || value is null) continue;
            var tags = value.TagList.Select(Clean).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var cleaned = new KeepSeedingIndexerSettings(value.PrivateOverride, tags, Clean(value.Category));
            if (!cleaned.IsEmpty) indexers[id] = cleaned;
        }

        var tagCategories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in TagCategoryMap)
        {
            if (Clean(key) is { } tag && Clean(value) is { } category) tagCategories[tag] = category;
        }

        return new KeepSeedingSettings(clients, indexers, tagCategories);
    }
}

/// <summary>Which of the three places a keep-seeding category came from, most specific first.</summary>
public static class KeepSeedingCategorySources
{
    public const string Tag = "tag";
    public const string Site = "site";
    public const string Client = "client";
}

/// <summary>
/// The answer for one struck torrent (#1240).
/// </summary>
/// <param name="IsPrivate">The client says the torrent is private, or its site is (definition or the person's override).</param>
/// <param name="IsTagged">Its site carries at least one seeding tag.</param>
/// <param name="Category">The most specific category that applies; null when none is set anywhere.</param>
/// <param name="CategorySource">One of <see cref="KeepSeedingCategorySources"/>.</param>
/// <param name="Tag">The tag whose category won, when the source is a tag.</param>
public sealed record KeepSeedingDecision(
    bool IsPrivate,
    bool IsTagged,
    string? Category,
    string? CategorySource,
    string? Tag)
{
    /// <summary>A private or tagged torrent is never deleted by a rule that keeps seeding.</summary>
    public bool KeepsSeeding => IsPrivate || IsTagged;

    public bool HasCategory => !string.IsNullOrWhiteSpace(Category);
}

public static class KeepSeedingResolver
{
    /// <summary>
    /// Decides whether a struck torrent is one to keep seeding and which category it goes to (#1240).
    /// </summary>
    /// <param name="clientId">The download client holding it.</param>
    /// <param name="indexerId">The site it came from, when Deluno knows.</param>
    /// <param name="clientSaysPrivate">The client's own private flag for the torrent; null when it did not say.</param>
    /// <param name="siteExpectsSharing">The site's stored or definition privacy is private or semi-private.</param>
    /// <remarks>
    /// Either signal saying private is enough, so a mistake can only protect an account, never expose one. The
    /// tag's category beats the site's, which beats the client's; with several tags the first one listed on the
    /// site that has a category wins.
    /// </remarks>
    public static KeepSeedingDecision Decide(
        KeepSeedingSettings settings,
        string clientId,
        string? indexerId,
        bool? clientSaysPrivate,
        bool siteExpectsSharing)
    {
        var site = settings.Indexer(indexerId);
        var sitePrivate = site?.PrivateOverride ?? siteExpectsSharing;
        var isPrivate = clientSaysPrivate == true || sitePrivate;
        var tags = site?.TagList ?? [];
        var isTagged = tags.Count > 0;

        foreach (var tag in tags)
        {
            if (settings.TagCategory(tag) is { } tagCategory)
            {
                return new KeepSeedingDecision(isPrivate, isTagged, tagCategory, KeepSeedingCategorySources.Tag, tag);
            }
        }

        if (!string.IsNullOrWhiteSpace(site?.Category))
        {
            return new KeepSeedingDecision(isPrivate, isTagged, site!.Category!.Trim(), KeepSeedingCategorySources.Site, null);
        }

        return settings.ClientCategory(clientId) is { } clientCategory
            ? new KeepSeedingDecision(isPrivate, isTagged, clientCategory, KeepSeedingCategorySources.Client, null)
            : new KeepSeedingDecision(isPrivate, isTagged, null, null, null);
    }
}

/// <summary>Why a struck torrent could not be moved to keep seeding (#1240).</summary>
public static class KeepSeedingProblemKinds
{
    /// <summary>The client refused the move (the category is missing, it is down, it answered with an error). Retried at every check.</summary>
    public const string Refused = "refused";

    /// <summary>No keep-seeding category is set on the tag, the site or the client. The torrent is left alone until one is.</summary>
    public const string NeedsCategory = "needs-category";
}

/// <summary>
/// One struck torrent Deluno meant to keep seeding and could not (#1240). Shown on System, Health and on the
/// Keep seeding screen with the reason; it goes away on its own when a retry works, the torrent recovers or leaves the
/// client, and a person can clear it.
/// </summary>
public sealed record KeepSeedingProblem(
    string Key,
    string ClientId,
    string ClientName,
    string ItemId,
    string ReleaseName,
    string Kind,
    string Message,
    string? Category,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset LastTriedUtc,
    int Attempts)
{
    public static string KeyFor(string clientId, string itemId) => $"{clientId}:{itemId}".ToLowerInvariant();
}
