using System.Globalization;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Integrations.MediaServers;
using Deluno.Libraries.Data;
using Deluno.Movies.Data;
using Deluno.Series.Data;

namespace Deluno.Retention;

/// <summary>
/// The conditions a rule can use that are not catalogue facts: what media
/// servers say has been watched (#494).
/// </summary>
public static class RetentionWatchFields
{
    public const string Watched = "watched";
    public const string LastPlayed = "lastPlayed";

    public static bool IsWatchField(string condition)
    {
        var field = condition.Split(':', 2)[0];
        return string.Equals(field, Watched, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(field, LastPlayed, StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<RetentionField> Fields =>
    [
        new(Watched, "Watched", "Whether anyone has finished it, on the media servers the rule reads.", "watching", "boolean", ["is"], null, NeedsMediaServer: true),
        new(LastPlayed, "Last played", "The most recent time anyone played it, on the media servers the rule reads.", "watching", "date", ["beyond", "within", "unset"], null, NeedsMediaServer: true)
    ];
}

/// <summary>A title a rule was asked about, and what came of it.</summary>
public sealed record RetentionCandidate(
    string EntityId,
    string MediaType,
    string Title,
    int? Year,
    string? ImdbId,
    long? SizeBytes,
    IReadOnlyList<string> Reasons,
    string Outcome,
    string? Message,
    IReadOnlyList<(MediaServerConnection Server, MediaServerItem Item)> ServerItems);

public sealed record RetentionEvaluation(
    IReadOnlyList<RetentionCandidate> Candidates,
    IReadOnlyList<string> UnreachableServers,
    IReadOnlyList<string> Problems)
{
    public IEnumerable<RetentionCandidate> Matched => Candidates.Where(item => item.Outcome == RetentionItemOutcomes.Matched);
}

/// <summary>
/// Works out which titles a rule selects, and why each one.
///
/// <para><b>Silence is not consent.</b> A rule that reads watch history, or that
/// removes titles from media servers, depends on those servers answering. If any
/// of them could not be asked, nothing is selected: every title that met the
/// catalogue conditions is listed as left out, with the server named. A server
/// that could not say whether a film was watched has not said it was.</para>
///
/// <para><b>Titles are matched to server items</b> first by file path - the
/// exact file for a film, the show's folder for a series - and then by TMDb or
/// IMDb id. A path match cannot pick the wrong film; an id match covers a server
/// that sees the files through a different mount.</para>
/// </summary>
public sealed class RetentionEvaluator(
    IMovieCatalogRepository movies,
    ISeriesCatalogRepository series,
    ILibrariesRepository libraries,
    IMediaServerRepository mediaServers,
    IMediaServerClientRegistry clients,
    TimeProvider timeProvider)
{
    private const int PageSize = 500;

    public static IReadOnlyList<string> Validate(SaveRetentionRuleRequest request, IReadOnlyList<MediaServerConnection> servers)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            problems.Add("Give the rule a name.");
        }

        var kind = request.MediaType == "tv" ? MediaKind.Series : MediaKind.Movie;
        var conditions = request.Conditions ?? [];
        if (conditions.Count == 0)
        {
            problems.Add("Add at least one condition. A rule with none would select everything.");
        }

        CatalogueFilters.Parse(kind, conditions.Where(condition => !RetentionWatchFields.IsWatchField(condition)), out var catalogueErrors);
        problems.AddRange(catalogueErrors);
        foreach (var condition in conditions.Where(RetentionWatchFields.IsWatchField))
        {
            if (ParseWatch(condition) is null)
            {
                problems.Add($"“{condition}” is not a watch condition Deluno understands.");
            }
        }

        var chosen = request.MediaServerIds ?? [];
        if ((conditions.Any(RetentionWatchFields.IsWatchField) || request.RemoveFromServers) && chosen.Count == 0)
        {
            problems.Add(conditions.Any(RetentionWatchFields.IsWatchField)
                ? "Choose the media servers whose watch history this rule reads."
                : "Choose the media servers to remove titles from, or turn that off.");
        }

        foreach (var id in chosen.Where(id => servers.All(server => server.Id != id)))
        {
            problems.Add($"There is no media server with id “{id}”.");
        }

        return problems;
    }

    public async Task<RetentionEvaluation> EvaluateAsync(RetentionRule rule, CancellationToken cancellationToken)
    {
        var kind = rule.MediaType == "tv" ? MediaKind.Series : MediaKind.Movie;
        var catalogueConditions = rule.Conditions.Where(condition => !RetentionWatchFields.IsWatchField(condition)).ToArray();
        var watchConditions = rule.Conditions.Where(RetentionWatchFields.IsWatchField).Select(ParseWatch).OfType<WatchCondition>().ToArray();
        var filters = CatalogueFilters.Parse(kind, catalogueConditions, out var parseErrors);
        if (parseErrors.Count > 0)
        {
            return new RetentionEvaluation([], [], parseErrors);
        }

        var titles = await ReadTitlesAsync(rule.MediaType, filters, cancellationToken);
        var catalogueReasons = catalogueConditions.Select(condition => Describe(kind, condition)).ToArray();

        var needsServers = watchConditions.Length > 0 || rule.RemoveFromServers;
        var configured = await mediaServers.ListAsync(cancellationToken);
        var chosen = configured.Where(server => rule.MediaServerIds.Contains(server.Id, StringComparer.OrdinalIgnoreCase)).ToArray();
        var unreachable = new List<string>();
        var serverItems = new List<(MediaServerConnection Server, IReadOnlyList<MediaServerItem> Items)>();
        foreach (var server in chosen)
        {
            if (!server.IsEnabled)
            {
                unreachable.Add($"{server.Name} is turned off");
                continue;
            }

            var client = clients.Find(server.Kind);
            var answer = client is null
                ? new MediaServerAnswer<IReadOnlyList<MediaServerItem>>.NotSupported($"{server.Name} is a kind of server this build cannot read.")
                : await client.ListItemsAsync(server, rule.MediaType, cancellationToken);
            switch (answer)
            {
                case MediaServerAnswer<IReadOnlyList<MediaServerItem>>.Reported reported:
                    serverItems.Add((server, reported.Value));
                    break;
                case MediaServerAnswer<IReadOnlyList<MediaServerItem>>.Unreachable failed:
                    unreachable.Add(Naming(server, failed.Reason));
                    break;
                case MediaServerAnswer<IReadOnlyList<MediaServerItem>>.NotSupported unsupported:
                    unreachable.Add(Naming(server, unsupported.Reason));
                    break;
            }
        }

        if (needsServers && rule.MediaServerIds.Count > chosen.Length)
        {
            unreachable.Add("A media server this rule reads has been removed from Deluno");
        }

        var now = timeProvider.GetUtcNow();
        var candidates = new List<RetentionCandidate>();
        foreach (var title in titles)
        {
            if (needsServers && unreachable.Count > 0)
            {
                // Why is said once, in the run's summary; each title only records
                // that it was left alone.
                candidates.Add(new RetentionCandidate(
                    title.Id, rule.MediaType, title.Title, title.Year, title.ImdbId, title.SizeBytes, catalogueReasons,
                    RetentionItemOutcomes.ExcludedUnreachable,
                    null,
                    []));
                continue;
            }

            var matches = serverItems
                .SelectMany(entry => entry.Items.Where(item => SameTitle(title, item)).Select(item => (entry.Server, Item: item)))
                .ToArray();
            var reasons = catalogueReasons.ToList();
            var holds = true;
            foreach (var condition in watchConditions)
            {
                var (met, reason) = condition.Evaluate(matches, now);
                reasons.Add(reason);
                holds &= met;
            }

            if (holds)
            {
                candidates.Add(new RetentionCandidate(
                    title.Id, rule.MediaType, title.Title, title.Year, title.ImdbId, title.SizeBytes, reasons,
                    RetentionItemOutcomes.Matched,
                    rule.RemoveFromServers && matches.Length > 0 ? $"Also on {string.Join(", ", matches.Select(match => match.Server.Name).Distinct())}." : null,
                    matches));
            }
        }

        return new RetentionEvaluation(candidates, unreachable, []);
    }

    /// <summary>A server's own sentence, without naming the server twice or ending in two full stops.</summary>
    private static string Naming(MediaServerConnection server, string reason)
    {
        var trimmed = reason.Trim().TrimEnd('.');
        return trimmed.Contains(server.Name, StringComparison.OrdinalIgnoreCase) ? trimmed : $"{server.Name}: {trimmed}";
    }

    private sealed record TitleFacts(string Id, string Title, int? Year, string? ImdbId, string? TmdbId, long? SizeBytes, IReadOnlyList<string> Paths, bool IsSeries);

    private async Task<IReadOnlyList<TitleFacts>> ReadTitlesAsync(string mediaType, CatalogueFilters filters, CancellationToken cancellationToken)
    {
        var allLibraries = (await libraries.ListLibrariesAsync(cancellationToken))
            .Where(library => (library.MediaType == "tv") == (mediaType == "tv"))
            .ToArray();
        var paths = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        void Remember(string id, string path)
        {
            if (!paths.TryGetValue(id, out var list)) paths[id] = list = [];
            list.Add(path);
        }

        var facts = new List<TitleFacts>();
        string? token = null;
        if (mediaType == "tv")
        {
            foreach (var library in allLibraries)
            {
                await foreach (var file in series.StreamTrackedFilesAsync(library.Id, cancellationToken)) Remember(file.SeriesId, file.FilePath);
            }

            do
            {
                var page = await series.ListPageAsync(new CatalogueQuery(Status: CatalogueStatusFilters.Downloaded, PageSize: PageSize, PageToken: token, Filters: filters), cancellationToken);
                facts.AddRange(page.Items.Select(item => new TitleFacts(
                    item.Id, item.Title, item.StartYear, item.ImdbId, TmdbId(item.MetadataProvider, item.MetadataProviderId), item.FileSizeBytes,
                    paths.TryGetValue(item.Id, out var held) ? held : [], IsSeries: true)));
                token = page.HasMore ? page.NextPageToken : null;
            }
            while (token is not null);
        }
        else
        {
            foreach (var library in allLibraries)
            {
                await foreach (var file in movies.StreamTrackedFilesAsync(library.Id, cancellationToken)) Remember(file.MovieId, file.FilePath);
            }

            do
            {
                var page = await movies.ListPageAsync(new CatalogueQuery(Status: CatalogueStatusFilters.Downloaded, PageSize: PageSize, PageToken: token, Filters: filters), cancellationToken);
                facts.AddRange(page.Items.Select(item => new TitleFacts(
                    item.Id, item.Title, item.ReleaseYear, item.ImdbId, TmdbId(item.MetadataProvider, item.MetadataProviderId), item.FileSizeBytes,
                    paths.TryGetValue(item.Id, out var held) ? held : [], IsSeries: false)));
                token = page.HasMore ? page.NextPageToken : null;
            }
            while (token is not null);
        }

        return facts;
    }

    private static string? TmdbId(string? provider, string? providerId)
        => string.Equals(provider, "tmdb", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(providerId) ? providerId : null;

    private static bool SameTitle(TitleFacts title, MediaServerItem item)
    {
        foreach (var serverPath in item.Paths.Select(NormalizePath))
        {
            foreach (var titlePath in title.Paths.Select(NormalizePath))
            {
                if (titlePath == serverPath) return true;
                if (title.IsSeries && titlePath.StartsWith(serverPath + "/", StringComparison.Ordinal)) return true;
            }
        }

        return (title.TmdbId is not null && string.Equals(title.TmdbId, item.TmdbId, StringComparison.OrdinalIgnoreCase)) ||
               (title.ImdbId is not null && string.Equals(title.ImdbId, item.ImdbId, StringComparison.OrdinalIgnoreCase));
    }

    internal static string NormalizePath(string path)
        => path.Trim().Replace('\\', '/').TrimEnd('/').ToLowerInvariant();

    /// <summary>A condition, in the words a person reads in a preview.</summary>
    internal static string Describe(MediaKind kind, string condition)
    {
        var parts = condition.Split(':', 3);
        var field = CatalogueFilterFields.Find(kind, parts[0]);
        var label = field?.Label ?? parts[0];
        var values = parts.Length > 2 ? parts[2].Split('|') : [];
        var unit = field?.ValueKind switch
        {
            CatalogueFilterValueKind.Gigabytes => " GB",
            CatalogueFilterValueKind.Minutes => " min",
            _ => string.Empty
        };
        var joined = string.Join(", ", values) + unit;
        var phrase = (parts.Length > 1 ? parts[1] : string.Empty).ToLowerInvariant() switch
        {
            "in" => $"is {joined}",
            "notin" => $"is not {joined}",
            "all" => $"includes {joined}",
            "is" => $"is {joined}",
            "isnot" => $"is not {joined}",
            "min" => $"is at least {joined}",
            "max" => $"is at most {joined}",
            "has" => $"contains “{joined}”",
            "nothas" => $"does not contain “{joined}”",
            "starts" => $"starts with “{joined}”",
            "ends" => $"ends with “{joined}”",
            "before" => $"is before {joined}",
            "after" => $"is after {joined}",
            "within" => $"is within the last {joined} days",
            "next" => $"is within the next {joined} days",
            "beyond" => $"is more than {joined} days ago",
            "set" => "is set",
            "unset" => "is not set",
            var other => $"{other} {joined}"
        };
        return $"{label} {phrase}";
    }

    private static WatchCondition? ParseWatch(string condition)
    {
        var parts = condition.Split(':', 3);
        var op = parts.Length > 1 ? parts[1].ToLowerInvariant() : string.Empty;
        var value = parts.Length > 2 ? parts[2] : null;
        if (string.Equals(parts[0], RetentionWatchFields.Watched, StringComparison.OrdinalIgnoreCase) && op == "is" &&
            bool.TryParse(value, out var watched))
        {
            return new WatchedCondition(watched);
        }

        if (string.Equals(parts[0], RetentionWatchFields.LastPlayed, StringComparison.OrdinalIgnoreCase))
        {
            if (op == "unset") return new LastPlayedCondition(op, 0);
            if (op is "beyond" or "within" && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) && days > 0)
            {
                return new LastPlayedCondition(op, days);
            }
        }

        return null;
    }

    private abstract record WatchCondition
    {
        public abstract (bool Met, string Reason) Evaluate(IReadOnlyList<(MediaServerConnection Server, MediaServerItem Item)> matches, DateTimeOffset now);

        protected static string WhoWatched(IReadOnlyList<(MediaServerConnection Server, MediaServerItem Item)> matches)
        {
            var played = matches.Where(match => match.Item.Watch.PlayedByAnyone).ToArray();
            if (played.Length == 0)
            {
                return matches.Count == 0 ? "not on any of the rule's media servers" : "nobody has watched it";
            }

            return string.Join("; ", played.Select(match => match.Item.Watch.PerUserKnown && match.Item.Watch.PlayedBy.Count > 0
                ? $"watched by {string.Join(", ", match.Item.Watch.PlayedBy)} on {match.Server.Name}"
                : $"watched on {match.Server.Name}"));
        }
    }

    private sealed record WatchedCondition(bool Watched) : WatchCondition
    {
        public override (bool Met, string Reason) Evaluate(IReadOnlyList<(MediaServerConnection Server, MediaServerItem Item)> matches, DateTimeOffset now)
        {
            var played = matches.Any(match => match.Item.Watch.PlayedByAnyone);
            return (played == Watched, $"{(Watched ? "Watched" : "Not watched")}: {WhoWatched(matches)}");
        }
    }

    private sealed record LastPlayedCondition(string Operator, int Days) : WatchCondition
    {
        public override (bool Met, string Reason) Evaluate(IReadOnlyList<(MediaServerConnection Server, MediaServerItem Item)> matches, DateTimeOffset now)
        {
            var last = matches.Select(match => match.Item.Watch.LastPlayedUtc).Where(when => when is not null).Max();
            var said = last is null ? "never played" : $"last played {last.Value:d MMM yyyy}";
            return Operator switch
            {
                "unset" => (last is null, $"Never played: {said}"),
                "beyond" => (last is not null && last < now.AddDays(-Days), $"Last played more than {Days} days ago: {said}"),
                _ => (last is not null && last >= now.AddDays(-Days), $"Last played within {Days} days: {said}")
            };
        }
    }
}
