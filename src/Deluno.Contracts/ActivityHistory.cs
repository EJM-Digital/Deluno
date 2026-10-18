using System.Text.Json;

namespace Deluno.Contracts;

/// <summary>
/// The kinds, sources and outcomes the Activity history can be filtered by (#1100). One place
/// decides which plain kind an event belongs to, so the API filter, the screen's menus and the
/// words on each row can never disagree. Every kind is a set of the categories
/// <see cref="ActivityEventLabels"/> already names, so the filter is an indexed category lookup.
/// </summary>
public static class ActivityHistory
{
    /// <summary>The category the turned-down release rows are written under (#1100).</summary>
    public const string TurnedDownCategory = "release.turned-down";

    public static class Types
    {
        public const string Searched = "search";
        public const string Grabbed = "grabbed";
        public const string TurnedDown = "turned-down";
        public const string Imported = "imported";
        public const string Subtitles = "subtitles";
        public const string Blocklisted = "blocklisted";
        public const string Removed = "removed";
        public const string Restored = "restored";
        public const string Failed = "failed";
        public const string Health = "health";
        public const string Other = "other";

        public static readonly IReadOnlyList<string> All = [Searched, TurnedDown, Grabbed, Imported, Subtitles, Blocklisted, Removed, Restored, Failed, Health, Other];

        public static string Label(string type) => type switch
        {
            Searched => "Searched",
            TurnedDown => "Turned down",
            Grabbed => "Grabbed and sent",
            Imported => "Imported",
            Subtitles => "Subtitles",
            Blocklisted => "Blocklisted",
            Removed => "Removed or deleted",
            Restored => "Restored",
            Failed => "Went wrong",
            Health => "Health",
            _ => "Other"
        };
    }

    public static class Sources
    {
        public const string Indexer = "indexer";
        public const string Client = "client";
        public const string Processor = "processor";
        public const string Connection = "connection";

        public static readonly IReadOnlyList<string> All = [Indexer, Client, Processor, Connection];

        public static string Label(string source) => source switch
        {
            Indexer => "An indexer",
            Client => "A download client",
            Processor => "A processor",
            _ => "Another connection"
        };
    }

    public static class Outcomes
    {
        public const string Good = "good";
        public const string Problem = "problem";
    }

    /// <summary>The plain kind of an event. A failure is found under "went wrong" however it is also named.</summary>
    public static string TypeOf(string? category)
    {
        var c = (category ?? string.Empty).Trim().ToLowerInvariant();
        if (c == TurnedDownCategory) return Types.TurnedDown;
        if (c.Contains("block-lifted") || c.Contains("restored")) return Types.Restored;
        if (c.Contains("block")) return Types.Blocklisted;
        if (c.StartsWith("subtitle", StringComparison.Ordinal)) return Types.Subtitles;
        if (c.Contains("rejected")) return c.StartsWith("download.grab", StringComparison.Ordinal) ? Types.Failed : Types.Removed;
        if (c.Contains("removed") || c.Contains("dismissed") || c.Contains("deleted") || c.Contains("cancelled") || c.Contains("cleared")) return Types.Removed;
        if (IsProblem(c)) return Types.Failed;
        if (c.Contains("import") || c.Contains("renamed")) return Types.Imported;
        if (c.Contains("dispatch") || c.StartsWith("release.", StringComparison.Ordinal) || c.Contains("grab") || c.StartsWith("download.", StringComparison.Ordinal)) return Types.Grabbed;
        if (c.StartsWith("health", StringComparison.Ordinal)) return Types.Health;
        if (c.StartsWith("search", StringComparison.Ordinal) || c.Contains(".search") || c.StartsWith("rss.", StringComparison.Ordinal)) return Types.Searched;
        return Types.Other;
    }

    /// <summary>Which kind of outside thing an event came from; null when Deluno did it on its own.</summary>
    public static string? SourceOf(string? category)
    {
        var c = (category ?? string.Empty).Trim().ToLowerInvariant();
        if (c == TurnedDownCategory || c.StartsWith("trackers.", StringComparison.Ordinal) || c.StartsWith("rss.", StringComparison.Ordinal)
            || c.Contains(".search") || c.EndsWith(".release.grabbed", StringComparison.Ordinal) || c.EndsWith(".release.force-grabbed", StringComparison.Ordinal))
        {
            return Sources.Indexer;
        }

        if (c.StartsWith("download", StringComparison.Ordinal) || c.StartsWith("retention.", StringComparison.Ordinal) || c.EndsWith("download-cancelled", StringComparison.Ordinal))
        {
            return Sources.Client;
        }

        if (c.StartsWith("processing", StringComparison.Ordinal) || c.StartsWith("processor", StringComparison.Ordinal))
        {
            return Sources.Processor;
        }

        if (c.StartsWith("media_server", StringComparison.Ordinal) || c.StartsWith("subtitle", StringComparison.Ordinal) || c.StartsWith("seerr", StringComparison.Ordinal)
            || c.StartsWith("intake", StringComparison.Ordinal) || c.StartsWith("connectivity", StringComparison.Ordinal))
        {
            return Sources.Connection;
        }

        return null;
    }

    public static string OutcomeOf(string? category)
        => IsProblem((category ?? string.Empty).Trim().ToLowerInvariant()) ? Outcomes.Problem : Outcomes.Good;

    private static readonly string[] ProblemWords =
    [
        "failed", "failure", "timeout", "timed-out", "unreachable", "unreadable", "stalled", "lost", "vanished", "missing", "files-gone",
        "abandoned", "dead-letter", "lease-expired", "setup-problem", "not-imported", "exhausted", "unmatched", "misplaced", "forbidden",
        "no-progress", "outage.started", "health.problem", "needs-attention", "script-failed", "rejected", "held-by", "given-up",
        "dead-link", "not-reported", "records-failed", "rate-limited", "provider.paused"
    ];

    private static bool IsProblem(string category)
    {
        if (category.Contains("resolved") || category.Contains("recovered") || category.Contains("outage.resolved") || category == "library.reachable")
        {
            return false;
        }

        foreach (var word in ProblemWords)
        {
            if (category.Contains(word, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Every known category of a kind, source or outcome: the indexed set a filter looks up.</summary>
    public static IReadOnlyList<string> CategoriesOfType(string type)
        => ActivityEventLabels.KnownCategories.Where(c => TypeOf(c) == type).OrderBy(c => c, StringComparer.Ordinal).ToArray();

    public static IReadOnlyList<string> CategoriesOfSource(string source)
        => ActivityEventLabels.KnownCategories.Where(c => SourceOf(c) == source).OrderBy(c => c, StringComparer.Ordinal).ToArray();

    public static IReadOnlyList<string> CategoriesOfOutcome(string outcome)
        => ActivityEventLabels.KnownCategories.Where(c => OutcomeOf(c) == outcome).OrderBy(c => c, StringComparer.Ordinal).ToArray();

    private static readonly string[] TitleKeys = ["title", "movieTitle", "seriesTitle", "itemTitle", "mediaTitle", "libraryItemTitle"];
    private static readonly string[] PathKeys =
    [
        "path", "filePath", "sourcePath", "destinationPath", "destination", "source", "importedPath", "importedFilePath", "outputPath",
        "videoPath", "originalPath", "newPath", "oldPath", "writtenPath", "targetPath", "recyclePath"
    ];
    private static readonly string[] SourceNameKeys = ["indexerName", "downloadClientName", "clientName", "processorName", "providerName", "connectionName", "serverName"];

    /// <summary>
    /// The title, file paths and outside source an event concerns, read once when the event is written
    /// from the facts its writer already put in the details; none of them is guessed from the sentence.
    /// </summary>
    public static (string? Title, string? FilePaths, string? SourceName) FactsFromDetails(string? detailsJson)
    {
        if (string.IsNullOrWhiteSpace(detailsJson) || detailsJson.Length > 65_536 || detailsJson[0] != '{')
        {
            return (null, null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(detailsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, null, null);
            }

            string? title = null, sourceName = null;
            var paths = new List<string>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String) continue;
                var value = property.Value.GetString();
                if (string.IsNullOrWhiteSpace(value)) continue;
                if (title is null && TitleKeys.Contains(property.Name, StringComparer.OrdinalIgnoreCase)) title = value;
                else if (sourceName is null && SourceNameKeys.Contains(property.Name, StringComparer.OrdinalIgnoreCase)) sourceName = value;
                else if (PathKeys.Contains(property.Name, StringComparer.OrdinalIgnoreCase) && !paths.Contains(value, StringComparer.OrdinalIgnoreCase)) paths.Add(value);
            }

            // A batch event (a rename of several titles) keeps each title's paths in a list of objects; read those too.
            foreach (var property in document.RootElement.EnumerateObject())
            {
                CollectNestedPaths(property.Value, 1, paths);
            }

            return (Clip(title, 300), paths.Count == 0 ? null : Clip(string.Join('\n', paths), 1500), Clip(sourceName, 200));
        }
        catch (JsonException)
        {
            return (null, null, null);
        }
    }

    private static void CollectNestedPaths(JsonElement element, int depth, List<string> paths)
    {
        if (depth > 5) return;
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) CollectNestedPaths(item, depth + 1, paths);
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    var value = property.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(value) && PathKeys.Contains(property.Name, StringComparer.OrdinalIgnoreCase)
                        && !paths.Contains(value, StringComparer.OrdinalIgnoreCase)) paths.Add(value);
                }
                else CollectNestedPaths(property.Value, depth + 1, paths);
            }
        }
    }

    private static string? Clip(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}

/// <summary>What a person asked the Activity history to show (#1100). Every part is optional and they combine.</summary>
public sealed record ActivityQuery(
    string? Type = null,
    string? Title = null,
    string? File = null,
    string? Source = null,
    string? SourceName = null,
    string? Outcome = null,
    DateTimeOffset? SinceUtc = null,
    DateTimeOffset? UntilUtc = null,
    string? ReasonGroup = null,
    string? RelatedEntityType = null,
    string? RelatedEntityId = null,
    ActivitySort? Sort = null)
{
    /// <summary>
    /// Turned-down releases are written by the hundred, so the plain newest-first list leaves them out;
    /// asking for them, or for a title, file, source or reason, brings them in.
    /// </summary>
    public bool IncludesTurnedDown =>
        Type == ActivityHistory.Types.TurnedDown
        || !string.IsNullOrWhiteSpace(Title)
        || !string.IsNullOrWhiteSpace(File)
        || !string.IsNullOrWhiteSpace(ReasonGroup)
        || !string.IsNullOrWhiteSpace(SourceName)
        || !string.IsNullOrWhiteSpace(RelatedEntityId);
}

/// <summary>
/// The column the Activity list is sorted by (#1119). The server sorts before it cuts the pages, so the order holds
/// across "Show more", not just in the rows on screen. No sort means newest first.
/// </summary>
public sealed record ActivitySort(string Field, bool Descending)
{
    public const string Title = "title";
    public const string What = "what";
    public const string When = "when";

    /// <summary>Reads the query-string form; a field the screen has no column for is ignored, so a stale link falls back to newest first.</summary>
    public static ActivitySort? Parse(string? field, string? direction)
    {
        var normalised = field?.Trim().ToLowerInvariant();
        if (normalised is not (Title or What or When))
        {
            return null;
        }

        return new ActivitySort(normalised, string.Equals(direction?.Trim(), "desc", StringComparison.OrdinalIgnoreCase));
    }
}
