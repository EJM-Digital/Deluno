using System.Globalization;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Integrations.MediaServers;

namespace Deluno.Retention;

/// <summary>A title a rule was asked about, and what came of it.</summary>
public sealed record RetentionCandidate(
    RetentionUnit Unit,
    IReadOnlyList<string> Reasons,
    string Outcome,
    string? Message,
    IReadOnlyList<RetentionServerMatch> ServerItems)
{
    public string EntityId => Unit.EntityId;

    public string MediaType => RetentionLevels.MediaType(Unit.Level);

    public string Title => Unit.Title;

    public int? Year => Unit.Year;

    public string? ImdbId => Unit.ImdbId;

    public long? SizeBytes => Unit.SizeBytes;
}

/// <summary>How many titles were left after a step; a block's own steps inside it.</summary>
public sealed record RetentionStepCount(int Count, IReadOnlyList<RetentionStepCount>? Steps = null);

/// <summary>Something on a media server that Deluno does not manage. Listed, never touched.</summary>
public sealed record RetentionUnmanagedItem(string Server, string Title, int? Year);

public sealed record RetentionEvaluation(
    IReadOnlyList<RetentionCandidate> Candidates,
    IReadOnlyList<string> UnreachableServers,
    IReadOnlyList<string> Problems,
    int Considered = 0,
    IReadOnlyList<RetentionStepCount>? StepCounts = null,
    IReadOnlyList<RetentionUnmanagedItem>? Unmanaged = null)
{
    public IEnumerable<RetentionCandidate> Matched => Candidates.Where(item => item.Outcome == RetentionItemOutcomes.Matched);
}

/// <summary>
/// Works out which titles a rule selects, and why each one (#494, DESIGN-008).
///
/// <para><b>Steps, top to bottom.</b> "Keep only" narrows the titles so far to
/// those a condition holds for; "Also include" adds the titles from the whole
/// library it holds for; a block runs its own steps from the whole library and
/// counts as one step. After every step the number left is recorded, so the
/// builder can show what each step did.</para>
///
/// <para><b>Silence is not consent.</b> If a server the rule reads could not be
/// asked, nothing is selected: every title is listed as left out, with the
/// server named. A server that could not say whether a film was watched has not
/// said it was.</para>
///
/// <para><b>Cannot say is not no.</b> A condition a server cannot answer neither
/// keeps a title nor adds one; the title is listed as undecided, with why.</para>
/// </summary>
public sealed class RetentionEvaluator(
    RetentionUnitReader units,
    IMediaServerRepository mediaServers,
    IMediaServerClientRegistry clients,
    IRetentionDiskSpace disk,
    TimeProvider timeProvider)
{
    public static IReadOnlyList<string> Validate(SaveRetentionRuleRequest request, IReadOnlyList<MediaServerConnection> servers)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            problems.Add("Give the rule a name.");
        }

        var level = RetentionLevels.Normalize(request.Level, request.MediaType);
        var steps = request.Steps ?? RetentionStepList.FromConditions(request.Conditions ?? []);
        problems.AddRange(RetentionStepList.ShapeProblems(steps));

        var chosen = (request.MediaServerIds ?? []).ToArray();
        foreach (var id in chosen.Where(id => servers.All(server => server.Id != id)))
        {
            problems.Add($"There is no media server with id “{id}”.");
        }

        var chosenServers = servers.Where(server => chosen.Contains(server.Id, StringComparer.OrdinalIgnoreCase)).ToArray();
        var conditions = RetentionStepList.Conditions(steps).Select(RetentionFieldCatalog.Upgrade).ToArray();
        CatalogueFilters.Parse(RetentionFieldCatalog.CatalogueKind(level), conditions.Where(condition => !RetentionFieldCatalog.IsRetentionField(condition)), out var catalogueErrors);
        problems.AddRange(catalogueErrors);

        foreach (var condition in conditions.Where(RetentionFieldCatalog.IsRetentionField))
        {
            var (field, op, value, problem) = Parse(condition);
            if (problem is not null)
            {
                problems.Add(problem);
                continue;
            }

            if (!field!.Levels.Contains(level))
            {
                problems.Add($"“{field.Label}” does not apply to {LevelNoun(level)}.");
            }

            if (field.Source == RetentionFieldSources.Server)
            {
                if (chosenServers.Length == 0)
                {
                    problems.Add($"“{field.Label}” comes from media servers. Choose the servers this rule reads.");
                }
                else if (chosenServers.All(server => field.UnsupportedOn?.Contains(server.Kind, StringComparer.OrdinalIgnoreCase) == true))
                {
                    problems.Add($"None of the chosen servers can tell “{field.Label}” ({string.Join(", ", chosenServers.Select(server => server.Name))}).");
                }
            }

            if (NeedsValue(op!) && string.IsNullOrWhiteSpace(value))
            {
                problems.Add($"“{field.Label}” needs a value to compare with.");
            }
        }

        if (request.WaitDays is { } wait && (wait < RetentionLimits.MinWaitDays || wait > RetentionLimits.MaxWaitDays))
        {
            problems.Add($"A title waits {RetentionLimits.MinWaitDays} to {RetentionLimits.MaxWaitDays} days before it goes. There is always a warning first.");
        }

        if (request.CheckIntervalHours is { } hours && (hours < RetentionLimits.MinCheckIntervalHours || hours > RetentionLimits.MaxCheckIntervalHours))
        {
            problems.Add($"A rule is checked every {RetentionLimits.MinCheckIntervalHours} to {RetentionLimits.MaxCheckIntervalHours} hours.");
        }

        if (request.RemoveFromServers && chosen.Length == 0)
        {
            problems.Add("Choose the media servers to tell when a title is removed, or turn that off.");
        }

        return problems.Distinct().ToArray();
    }

    /// <param name="cachedLibraries">
    /// Server listings already read in the last minutes, for the builder's live
    /// counts. A check that can remove anything always passes null and reads fresh.
    /// </param>
    public async Task<RetentionEvaluation> EvaluateAsync(
        RetentionRule rule,
        CancellationToken cancellationToken,
        Func<MediaServerConnection, string, CancellationToken, Task<MediaServerAnswer<MediaServerLibrary>>>? cachedLibraries = null)
    {
        var level = rule.Level;
        var steps = rule.Steps;
        var conditions = RetentionStepList.Conditions(steps).ToArray();
        var configured = await mediaServers.ListAsync(cancellationToken);
        var problems = Validate(
            new SaveRetentionRuleRequest(rule.Name, level, rule.IsEnabled, steps, rule.MediaServerIds, rule.RemoveFromServers, rule.WaitDays, rule.RemovalMode, rule.CheckIntervalHours),
            configured);
        if (problems.Count > 0)
        {
            return new RetentionEvaluation([], [], problems);
        }

        var readsServers = conditions.Any(condition => condition.StartsWith("server.", StringComparison.OrdinalIgnoreCase));
        var needsServers = readsServers || rule.RemoveFromServers;
        var chosen = configured.Where(server => rule.MediaServerIds.Contains(server.Id, StringComparer.OrdinalIgnoreCase)).ToArray();
        var unreachable = new List<string>();
        var indexes = new List<RetentionServerIndex>();
        foreach (var server in chosen)
        {
            if (!server.IsEnabled)
            {
                unreachable.Add($"{server.Name} is turned off");
                continue;
            }

            var client = clients.Find(server.Kind);
            var answer = client is null
                ? new MediaServerAnswer<MediaServerLibrary>.NotSupported($"{server.Name} is a kind of server this build cannot read.")
                : cachedLibraries is not null
                    ? await cachedLibraries(server, RetentionLevels.MediaType(level), cancellationToken)
                    : await client.ListItemsAsync(server, RetentionLevels.MediaType(level), cancellationToken);
            switch (answer)
            {
                case MediaServerAnswer<MediaServerLibrary>.Reported reported:
                    indexes.Add(new RetentionServerIndex(server, reported.Value));
                    break;
                case MediaServerAnswer<MediaServerLibrary>.Unreachable failed:
                    unreachable.Add(Naming(server, failed.Reason));
                    break;
                case MediaServerAnswer<MediaServerLibrary>.NotSupported unsupported:
                    unreachable.Add(Naming(server, unsupported.Reason));
                    break;
            }
        }

        if (needsServers && rule.MediaServerIds.Count > chosen.Length)
        {
            unreachable.Add("A media server this rule reads has been removed from Deluno");
        }

        var all = await units.ReadAsync(level, cancellationToken);
        var answers = new RetentionFieldAnswers(indexes, disk, timeProvider.GetUtcNow());

        // Library filter conditions are asked of the catalogue once each; a
        // season or episode carries its show's answer.
        var catalogue = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        var kind = RetentionFieldCatalog.CatalogueKind(level);
        foreach (var condition in conditions.Select(RetentionFieldCatalog.Upgrade).Where(condition => !RetentionFieldCatalog.IsRetentionField(condition)).Distinct(StringComparer.Ordinal))
        {
            var filters = CatalogueFilters.Parse(kind, [condition], out _);
            catalogue[condition] = await units.CatalogueMatchesAsync(kind, filters, cancellationToken);
        }

        RetentionAnswer Ask(RetentionUnit unit, string raw)
        {
            var condition = RetentionFieldCatalog.Upgrade(raw);
            if (!RetentionFieldCatalog.IsRetentionField(condition))
            {
                var sentence = Describe(kind, condition);
                return catalogue[condition].Contains(unit.CatalogueId) ? RetentionAnswer.Yes(sentence) : RetentionAnswer.No(sentence);
            }

            var (field, op, value, _) = Parse(condition);
            return answers.Answer(unit, field!, op!, value);
        }

        var outcome = Run(steps, all, Ask);

        var candidates = new List<RetentionCandidate>();
        var serverMatches = new Dictionary<string, RetentionServerMatch[]>(StringComparer.OrdinalIgnoreCase);
        RetentionServerMatch[] MatchesFor(RetentionUnit unit)
        {
            if (!serverMatches.TryGetValue(unit.EntityId, out var found))
            {
                serverMatches[unit.EntityId] = found = indexes.Select(index => index.Find(unit)).OfType<RetentionServerMatch>().ToArray();
            }

            return found;
        }

        foreach (var unit in all)
        {
            if (outcome.Selected.TryGetValue(unit.EntityId, out var reasons))
            {
                if (needsServers && unreachable.Count > 0)
                {
                    // Why is said once, in the run's summary; each title only
                    // records that it was left alone.
                    candidates.Add(new RetentionCandidate(unit, reasons, RetentionItemOutcomes.ExcludedUnreachable, null, []));
                    continue;
                }

                var matches = MatchesFor(unit);
                candidates.Add(new RetentionCandidate(
                    unit,
                    reasons,
                    RetentionItemOutcomes.Matched,
                    rule.RemoveFromServers && matches.Length > 0 ? $"Also on {string.Join(", ", matches.Select(match => match.Server.Name).Distinct())}." : null,
                    matches));
            }
            else if (outcome.Undecided.TryGetValue(unit.EntityId, out var why))
            {
                // With a server unreachable, what could not be decided is what
                // that server would have decided: left alone, and said so once.
                candidates.Add(needsServers && unreachable.Count > 0
                    ? new RetentionCandidate(unit, [], RetentionItemOutcomes.ExcludedUnreachable, null, [])
                    : new RetentionCandidate(unit, [why], RetentionItemOutcomes.Undecided, why, []));
            }
        }

        // What the servers hold that Deluno does not: listed so it can be
        // brought in, and never acted on.
        var unmanaged = new List<RetentionUnmanagedItem>();
        if (level is RetentionLevels.Movie or RetentionLevels.Show && unreachable.Count == 0)
        {
            foreach (var index in indexes)
            {
                var matchedIds = all.Select(unit => index.Find(unit)?.Item.ServerItemId).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
                unmanaged.AddRange(index.Unmatched(matchedIds).Select(item => new RetentionUnmanagedItem(index.Server.Name, item.Title, item.Year)));
            }
        }

        return new RetentionEvaluation(candidates, unreachable, [], all.Count, outcome.Counts, unmanaged);
    }

    private sealed record StepOutcome(
        Dictionary<string, IReadOnlyList<string>> Selected,
        Dictionary<string, string> Undecided,
        IReadOnlyList<RetentionStepCount> Counts);

    /// <summary>Runs steps from the whole library.</summary>
    private static StepOutcome Run(IReadOnlyList<RetentionStep> steps, IReadOnlyList<RetentionUnit> all, Func<RetentionUnit, string, RetentionAnswer> ask)
    {
        Dictionary<string, IReadOnlyList<string>>? current = null;
        var undecided = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var counts = new List<RetentionStepCount>();

        foreach (var step in steps)
        {
            // What this step holds for, over the whole library, with why.
            Dictionary<string, IReadOnlyList<string>> holds;
            Dictionary<string, string> cannotSay;
            IReadOnlyList<RetentionStepCount>? inner = null;
            if (step.Type == RetentionStep.BlockType)
            {
                var block = Run(step.Steps ?? [], all, ask);
                holds = block.Selected;
                cannotSay = block.Undecided;
                inner = block.Counts;
            }
            else
            {
                holds = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
                cannotSay = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                // A Keep only step only needs asking about what is left.
                var asked = step.Op == RetentionStep.Keep && current is not null
                    ? all.Where(unit => current.ContainsKey(unit.EntityId))
                    : all;
                foreach (var unit in asked)
                {
                    var answer = ask(unit, step.Condition!);
                    if (answer.Holds == true) holds[unit.EntityId] = [answer.Sentence];
                    else if (answer.Holds is null) cannotSay[unit.EntityId] = answer.Sentence;
                }
            }

            if (step.Op == RetentionStep.Include || current is null)
            {
                var start = current ?? new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
                if (current is null && step.Op == RetentionStep.Keep)
                {
                    // The first step starts from the whole library either way.
                    start = new Dictionary<string, IReadOnlyList<string>>(holds, StringComparer.OrdinalIgnoreCase);
                }
                else
                {
                    foreach (var (id, why) in holds)
                    {
                        start.TryAdd(id, why);
                    }
                }

                current = start;
                foreach (var (id, why) in cannotSay.Where(pair => !current.ContainsKey(pair.Key)))
                {
                    undecided.TryAdd(id, why);
                }
            }
            else
            {
                var kept = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var (id, why) in current)
                {
                    if (holds.TryGetValue(id, out var more))
                    {
                        kept[id] = why.Concat(more).ToArray();
                    }
                    else if (cannotSay.TryGetValue(id, out var reason))
                    {
                        undecided[id] = reason;
                    }
                }

                current = kept;
            }

            foreach (var id in current.Keys)
            {
                undecided.Remove(id);
            }

            counts.Add(new RetentionStepCount(current.Count, inner));
        }

        return new StepOutcome(current ?? new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase), undecided, counts);
    }

    private static (RetentionFieldDefinition? Field, string? Op, string? Value, string? Problem) Parse(string condition)
    {
        var parts = condition.Split(':', 3);
        var field = RetentionFieldCatalog.Find(parts[0]);
        if (field is null)
        {
            return (null, null, null, $"There is no “{parts[0]}” for a rule to use.");
        }

        var op = parts.Length > 1 ? parts[1].Trim().ToLowerInvariant() : string.Empty;
        if (!field.Operators.Contains(op))
        {
            return (field, null, null, $"“{field.Label}” cannot be compared with “{op}”.");
        }

        return (field, op, parts.Length > 2 ? parts[2] : null, null);
    }

    private static bool NeedsValue(string op)
        => op is not ("set" or "unset" or RetentionOperators.Anyone or RetentionOperators.Nobody or RetentionOperators.Everyone);

    private static string LevelNoun(string level)
        => level switch
        {
            RetentionLevels.Movie => "movies",
            RetentionLevels.Show => "whole shows",
            RetentionLevels.Season => "seasons",
            _ => "episodes"
        };

    /// <summary>A server's own sentence, without naming the server twice or ending in two full stops.</summary>
    private static string Naming(MediaServerConnection server, string reason)
    {
        var trimmed = reason.Trim().TrimEnd('.');
        return trimmed.Contains(server.Name, StringComparison.OrdinalIgnoreCase) ? trimmed : $"{server.Name}: {trimmed}";
    }

    internal static string NormalizePath(string path) => RetentionServerIndex.NormalizePath(path);

    /// <summary>A library filter condition, in the words a person reads in a preview.</summary>
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
        return string.Create(CultureInfo.InvariantCulture, $"{label} {phrase}");
    }
}
