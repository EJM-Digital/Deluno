using System.Globalization;
using Deluno.Contracts;
using Deluno.Integrations.MediaServers;
using Deluno.Jobs.Data;
using Deluno.Media;
using Deluno.Movies;
using Deluno.Movies.Data;
using Deluno.Series;
using Deluno.Series.Data;
using Microsoft.Extensions.Logging;

namespace Deluno.Retention;

/// <summary>
/// Previews and cleanups (#494).
///
/// <para><b>Nothing is removed that was not listed first.</b> A cleanup names the
/// preview it acts on and removes only titles that preview listed and that still
/// match now - a title that changed in between (watched again, upgraded, already
/// gone) is recorded as changed and left alone. And if a server the rule
/// depends on cannot be asked at cleanup time, nothing at all is removed.</para>
///
/// <para><b>A cleanup is the whole deletion.</b> The title goes through the same
/// Remove path a person uses, with its files to the recycle bin; an exclusion
/// with the rule's name, the date and the reasons keeps every list and
/// collection from adding it back; and the servers the rule reads are told it is
/// gone. No release is blocklisted - nothing was wrong with it.</para>
/// </summary>
public sealed class RetentionService(
    IRetentionRepository repository,
    RetentionEvaluator evaluator,
    TitleRemovalService removal,
    IMovieCatalogRepository movies,
    ISeriesCatalogRepository series,
    IUnifiedExclusionRepository exclusions,
    IMediaServerClientRegistry clients,
    IActivityFeedRepository activity,
    TimeProvider timeProvider,
    ILogger<RetentionService> logger)
{
    public async Task<RetentionRun> PreviewAsync(RetentionRule rule, string trigger, CancellationToken cancellationToken)
    {
        var started = timeProvider.GetUtcNow();
        var evaluation = await evaluator.EvaluateAsync(rule, cancellationToken);
        var items = evaluation.Candidates.Select(ToItem).ToArray();
        var matched = items.Where(item => item.Outcome == RetentionItemOutcomes.Matched).ToArray();
        var excluded = items.Count(item => item.Outcome == RetentionItemOutcomes.ExcludedUnreachable);
        var bytes = matched.Sum(item => item.SizeBytes ?? 0);

        var summary = evaluation.Problems.Count > 0
            ? $"The rule could not be run: {string.Join(" ", evaluation.Problems)}"
            : evaluation.UnreachableServers.Count > 0
                ? $"Nothing selected: {string.Join("; ", evaluation.UnreachableServers)}. A server that could not be asked has not said anything was watched, so {excluded} {Titles(excluded)} {(excluded == 1 ? "was" : "were")} left alone."
                : matched.Length == 0
                    ? "Nothing matches this rule right now."
                    : $"{matched.Length} {Titles(matched.Length)} would be removed, freeing {FormatBytes(bytes)}.";

        var run = new RetentionRun(
            Guid.CreateVersion7().ToString("N"), rule.Id, rule.Name, RetentionRunKinds.Preview, trigger, null,
            started, timeProvider.GetUtcNow(), matched.Length, excluded, 0, 0, bytes, summary, items);
        await repository.SaveRunAsync(run, cancellationToken);
        return run;
    }

    public async Task<RetentionRun> CleanupAsync(
        RetentionRule rule,
        RetentionRun preview,
        IReadOnlyList<string>? entityIds,
        string trigger,
        CancellationToken cancellationToken)
    {
        var started = timeProvider.GetUtcNow();
        var listed = (preview.Items ?? [])
            .Where(item => item.Outcome == RetentionItemOutcomes.Matched)
            .Where(item => entityIds is null || entityIds.Contains(item.EntityId, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        var evaluation = await evaluator.EvaluateAsync(rule, cancellationToken);
        var stillMatching = evaluation.Matched.ToDictionary(candidate => candidate.EntityId, StringComparer.OrdinalIgnoreCase);
        var items = new List<RetentionRunItem>();

        if (evaluation.Problems.Count > 0 || evaluation.UnreachableServers.Count > 0)
        {
            var why = evaluation.Problems.Count > 0
                ? string.Join(" ", evaluation.Problems)
                : string.Join("; ", evaluation.UnreachableServers);
            items.AddRange(listed.Select(item => item with { Outcome = RetentionItemOutcomes.ExcludedUnreachable, Message = $"Nothing was removed: {why}." }));
            return await FinishAsync(rule, preview, trigger, started, items, $"Nothing was removed: {why}.", cancellationToken);
        }

        var removedOn = started.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
        foreach (var item in listed)
        {
            if (!stillMatching.TryGetValue(item.EntityId, out var candidate))
            {
                items.Add(item with { Outcome = RetentionItemOutcomes.Changed, Message = "No longer matches the rule, so it was left alone." });
                continue;
            }

            try
            {
                var reason = $"cleaned up by retention rule “{rule.Name}” on {removedOn} ({string.Join("; ", candidate.Reasons)})";
                var target = await TargetAsync(rule.MediaType, item.EntityId, cancellationToken);
                if (target is null)
                {
                    items.Add(item with { Outcome = RetentionItemOutcomes.Changed, Message = "Already gone from Deluno." });
                    continue;
                }

                await removal.RemoveAsync(target.Value.Title, new TitleRemovalOptions(DeleteFiles: true, AddImportListExclusion: false, Reason: reason), cancellationToken);

                await exclusions.UpsertAsync(
                    new UpsertMediaExclusionRequest(
                        MediaType: rule.MediaType,
                        SourceKind: MediaExclusionSourceKinds.Retention,
                        SourceId: rule.Id,
                        SourceName: rule.Name,
                        Provider: target.Value.TmdbId is null ? "retention" : $"tmdb:{target.Value.TmdbId}",
                        EntryKey: EntryKey(candidate.Title, candidate.Year, candidate.ImdbId),
                        Title: candidate.Title,
                        Year: candidate.Year,
                        ImdbId: candidate.ImdbId,
                        DurationDays: null,
                        Reason: char.ToUpperInvariant(reason[0]) + reason[1..]),
                    cancellationToken);

                var serverNotes = new List<string>();
                if (rule.RemoveFromServers)
                {
                    foreach (var (server, serverItem) in candidate.ServerItems)
                    {
                        var client = clients.Find(server.Kind);
                        var answer = client is null
                            ? new MediaServerAnswer<MediaServerRemoval>.NotSupported($"{server.Name} cannot be updated by this build.")
                            : await client.RemoveAsync(server, serverItem, cancellationToken);
                        serverNotes.Add(answer switch
                        {
                            MediaServerAnswer<MediaServerRemoval>.Reported done => done.Value.Message,
                            MediaServerAnswer<MediaServerRemoval>.Unreachable unreachable => $"{server.Name} was not updated: {unreachable.Reason} It will drop the title on its next library scan.",
                            MediaServerAnswer<MediaServerRemoval>.NotSupported unsupported => $"{server.Name} was not updated: {unsupported.Reason}",
                            _ => $"{server.Name} was not updated."
                        });
                    }
                }

                items.Add(item with
                {
                    Reasons = candidate.Reasons,
                    Outcome = RetentionItemOutcomes.Removed,
                    Message = serverNotes.Count == 0 ? "Removed; files are in the recycle bin." : $"Removed; files are in the recycle bin. {string.Join(" ", serverNotes)}"
                });
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Retention rule {Rule} could not remove {Title}.", rule.Name, item.Title);
                items.Add(item with { Outcome = RetentionItemOutcomes.Failed, Message = exception.Message });
            }
        }

        var removed = items.Where(item => item.Outcome == RetentionItemOutcomes.Removed).ToArray();
        var failed = items.Count(item => item.Outcome == RetentionItemOutcomes.Failed);
        var summary = $"Removed {removed.Length} {Titles(removed.Length)}, freeing {FormatBytes(removed.Sum(item => item.SizeBytes ?? 0))}"
            + (failed > 0 ? $"; {failed} could not be removed" : string.Empty)
            + (items.Count - removed.Length - failed > 0 ? $"; {items.Count - removed.Length - failed} left alone because they changed" : string.Empty)
            + ".";
        return await FinishAsync(rule, preview, trigger, started, items, summary, cancellationToken);
    }

    private async Task<RetentionRun> FinishAsync(
        RetentionRule rule,
        RetentionRun preview,
        string trigger,
        DateTimeOffset started,
        IReadOnlyList<RetentionRunItem> items,
        string summary,
        CancellationToken cancellationToken)
    {
        var removed = items.Where(item => item.Outcome == RetentionItemOutcomes.Removed).ToArray();
        var run = new RetentionRun(
            Guid.CreateVersion7().ToString("N"), rule.Id, rule.Name, RetentionRunKinds.Cleanup, trigger, preview.Id,
            started, timeProvider.GetUtcNow(),
            preview.MatchedCount,
            items.Count(item => item.Outcome == RetentionItemOutcomes.ExcludedUnreachable),
            removed.Length,
            items.Count(item => item.Outcome == RetentionItemOutcomes.Failed),
            removed.Sum(item => item.SizeBytes ?? 0),
            summary,
            items);
        await repository.SaveRunAsync(run, cancellationToken);
        await activity.RecordActivityAsync(
            "retention.cleanup",
            $"Retention rule “{rule.Name}”: {summary}",
            System.Text.Json.JsonSerializer.Serialize(new { RuleId = rule.Id, RunId = run.Id, PreviewRunId = preview.Id, Trigger = trigger }),
            null,
            "retention-rule",
            rule.Id,
            cancellationToken);
        return run;
    }

    private async Task<(TitleToRemove Title, string? TmdbId)?> TargetAsync(string mediaType, string entityId, CancellationToken cancellationToken)
    {
        if (mediaType == "tv")
        {
            var show = await series.GetByIdAsync(entityId, cancellationToken);
            return show is null ? null : (SeriesRemoval.Target(show, series), Tmdb(show.MetadataProvider, show.MetadataProviderId));
        }

        var movie = await movies.GetByIdAsync(entityId, cancellationToken);
        return movie is null ? null : (MovieRemoval.Target(movie, movies), Tmdb(movie.MetadataProvider, movie.MetadataProviderId));
    }

    private static string? Tmdb(string? provider, string? id)
        => string.Equals(provider, "tmdb", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(id) ? id : null;

    /// <summary>The key an import list entry is looked up by, so a list finds the exclusion.</summary>
    internal static string EntryKey(string title, int? year, string? imdbId)
        => !string.IsNullOrWhiteSpace(imdbId)
            ? $"imdb:{imdbId.Trim().ToLowerInvariant()}"
            : $"title:{title.Trim().ToLowerInvariant()}:{year?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}";

    private static RetentionRunItem ToItem(RetentionCandidate candidate)
        => new(candidate.EntityId, candidate.MediaType, candidate.Title, candidate.Year, candidate.SizeBytes, candidate.Reasons, candidate.Outcome, candidate.Message);

    private static string Titles(int count) => count == 1 ? "title" : "titles";

    internal static string FormatBytes(long bytes)
        => bytes >= 1L << 30
            ? $"{bytes / (double)(1L << 30):0.#} GB"
            : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0.#} MB" : $"{bytes / 1024.0:0.#} KB";
}
