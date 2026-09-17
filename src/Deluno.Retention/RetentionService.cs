using System.Globalization;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Integrations.MediaServers;
using Deluno.Jobs.Data;
using Deluno.Libraries.Data;
using Deluno.Media;
using Deluno.Movies;
using Deluno.Movies.Data;
using Deluno.Notifications;
using Deluno.Platform;
using Deluno.Series;
using Deluno.Series.Data;
using Microsoft.Extensions.Logging;

namespace Deluno.Retention;

/// <summary>
/// Leaving soon, then gone (#494, DESIGN-008).
///
/// <para><b>A check.</b> The rule is asked what it selects now. A title it
/// selects that nobody kept is marked Leaving soon with its removal date; a
/// marked title it no longer selects, or that somebody kept, is cleared. When a
/// title's day has come the check has just confirmed it still matches, and it is
/// removed - or, for a rule that asks first, waits in Ready to remove.</para>
///
/// <para><b>Silence is not consent.</b> If a server the rule reads could not be
/// asked, nothing is marked, cleared or removed: marks that are due are held,
/// and the check says which server. A title a step could not decide is held
/// too.</para>
///
/// <para><b>A removal is the whole deletion.</b> A movie or show goes through
/// the same Remove a person uses, files to the recycle bin, with an exclusion
/// naming the rule, the date and the reasons. A season or episode loses only
/// its files, and is recorded as removed by the rule and no longer wanted. The
/// servers the rule reads are told. No release is blocklisted.</para>
/// </summary>
public sealed class RetentionService(
    IRetentionRepository repository,
    RetentionEvaluator evaluator,
    TitleRemovalService removal,
    IMovieCatalogRepository movies,
    ISeriesCatalogRepository series,
    ILibrariesRepository libraries,
    IRecycleBinService recycleBin,
    IUnifiedExclusionRepository exclusions,
    IMediaServerClientRegistry clients,
    IActivityFeedRepository activity,
    IOutboundNotificationService notifications,
    TimeProvider timeProvider,
    ILogger<RetentionService> logger)
{
    public const string LeavingSoonEvent = "retention.leaving-soon";
    public const string RemovedEvent = "retention.removed";
    public const string WaitingEvent = "retention.waiting";
    public const string FailedEvent = "retention.failed";

    public async Task<RetentionRun> CheckAsync(RetentionRule rule, string trigger, CancellationToken cancellationToken)
    {
        var started = timeProvider.GetUtcNow();
        await repository.MarkCheckedAsync(rule.Id, started, cancellationToken);
        var evaluation = await evaluator.EvaluateAsync(rule, cancellationToken);
        var marks = (await repository.ListMarksAsync(rule.Id, cancellationToken)).ToDictionary(mark => mark.EntityId, StringComparer.OrdinalIgnoreCase);
        var items = new List<RetentionRunItem>();

        if (evaluation.Problems.Count > 0)
        {
            var why = string.Join(" ", evaluation.Problems);
            await NotifyAsync(FailedEvent, $"Retention rule “{rule.Name}” could not run", why, rule, cancellationToken);
            return await FinishAsync(rule, RetentionRunKinds.Check, trigger, started, marks.Count, items, 0, $"The rule could not run: {why}", cancellationToken);
        }

        if (evaluation.UnreachableServers.Count > 0)
        {
            // Nothing is known, so nothing changes; what was due is said to be waiting.
            var why = string.Join("; ", evaluation.UnreachableServers.Select(reason => reason.TrimEnd('.')));
            var due = marks.Values.Where(mark => mark.RemoveAfterUtc <= started).ToArray();
            foreach (var mark in due)
            {
                await repository.UpsertMarkAsync(mark with { State = RetentionMarkStates.Held, StateReason = why, UpdatedUtc = started }, cancellationToken);
                items.Add(Item(mark, RetentionItemOutcomes.Held, $"Not removed: {why}."));
            }

            if (due.Length > 0)
            {
                await NotifyAsync(WaitingEvent, $"{Titles(due.Length)} could not be removed yet", $"Retention rule “{rule.Name}”: {why}. {List(due.Select(mark => mark.Title))}.", rule, cancellationToken);
            }

            return await FinishAsync(rule, RetentionRunKinds.Check, trigger, started, marks.Count, items, 0,
                $"Nothing changed: {why}. {Titles(marks.Count)} leaving soon wait until every server can be asked.", cancellationToken);
        }

        var keeps = (await repository.ListKeepsAsync(cancellationToken))
            .Where(keep => keep.RuleId is null || string.Equals(keep.RuleId, rule.Id, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(keep => keep.EntityId, StringComparer.OrdinalIgnoreCase);
        var candidates = evaluation.Candidates.ToDictionary(candidate => candidate.EntityId, StringComparer.OrdinalIgnoreCase);

        // Cleared: kept, or no longer selected. A title a step could not decide
        // keeps its mark and is held, because not knowing is not a change.
        foreach (var mark in marks.Values.ToArray())
        {
            if (keeps.TryGetValue(mark.EntityId, out var keep))
            {
                await repository.DeleteMarkAsync(rule.Id, mark.EntityId, cancellationToken);
                marks.Remove(mark.EntityId);
                items.Add(Item(mark, RetentionItemOutcomes.Cleared, keep.RuleId is null ? "Kept from every rule." : "Kept from this rule."));
                continue;
            }

            candidates.TryGetValue(mark.EntityId, out var candidate);
            if (candidate?.Outcome == RetentionItemOutcomes.Undecided)
            {
                var holding = mark with { State = RetentionMarkStates.Held, StateReason = candidate.Message, UpdatedUtc = started };
                await repository.UpsertMarkAsync(holding, cancellationToken);
                marks[mark.EntityId] = holding;
                continue;
            }

            if (candidate?.Outcome != RetentionItemOutcomes.Matched)
            {
                await repository.DeleteMarkAsync(rule.Id, mark.EntityId, cancellationToken);
                marks.Remove(mark.EntityId);
                items.Add(Item(mark, RetentionItemOutcomes.Cleared, "No longer matches the rule."));
            }
        }

        // Marked: selected and not kept.
        var newlyMarked = new List<RetentionMark>();
        foreach (var candidate in evaluation.Matched)
        {
            if (keeps.ContainsKey(candidate.EntityId))
            {
                continue;
            }

            if (marks.TryGetValue(candidate.EntityId, out var existing))
            {
                var wasHeld = existing.State == RetentionMarkStates.Held;
                var refreshed = existing with
                {
                    Title = candidate.Title,
                    SizeBytes = candidate.SizeBytes,
                    Reasons = candidate.Reasons,
                    EpisodeIds = candidate.Unit.Part?.EpisodeIds ?? [],
                    State = wasHeld ? RetentionMarkStates.Leaving : existing.State,
                    StateReason = wasHeld ? null : existing.StateReason,
                    UpdatedUtc = started
                };
                await repository.UpsertMarkAsync(refreshed, cancellationToken);
                marks[candidate.EntityId] = refreshed;
                continue;
            }

            var mark = new RetentionMark(
                rule.Id, rule.Name, candidate.EntityId, rule.Level, candidate.Unit.ShowId, candidate.Unit.SeasonNumber,
                candidate.Unit.Part?.EpisodeIds ?? [], candidate.Title, candidate.Year, candidate.SizeBytes, candidate.Reasons,
                started, started.AddDays(rule.WaitDays), RetentionMarkStates.Leaving, null, started);
            await repository.UpsertMarkAsync(mark, cancellationToken);
            marks[candidate.EntityId] = mark;
            newlyMarked.Add(mark);
            items.Add(Item(mark, RetentionItemOutcomes.Marked, $"Leaving soon: goes on {Day(mark.RemoveAfterUtc)} unless it stops matching or is kept."));
        }

        // Due: the day has come, and this check has just confirmed it still matches.
        var removed = new List<RetentionRunItem>();
        var ready = new List<RetentionMark>();
        long freed = 0;
        foreach (var mark in marks.Values.Where(mark => mark.RemoveAfterUtc <= started && mark.State != RetentionMarkStates.Held).ToArray())
        {
            if (rule.RemovalMode == RetentionRemovalModes.Ask)
            {
                if (mark.State != RetentionMarkStates.Ready)
                {
                    await repository.UpsertMarkAsync(mark with { State = RetentionMarkStates.Ready, UpdatedUtc = started }, cancellationToken);
                    ready.Add(mark);
                    items.Add(Item(mark, RetentionItemOutcomes.Ready, "Ready to remove: waiting for you to confirm."));
                }

                continue;
            }

            var outcome = await RemoveAsync(rule, mark, candidates[mark.EntityId], started, cancellationToken);
            items.Add(outcome);
            if (outcome.Outcome is RetentionItemOutcomes.Removed or RetentionItemOutcomes.Cleared)
            {
                marks.Remove(mark.EntityId);
            }

            if (outcome.Outcome == RetentionItemOutcomes.Removed)
            {
                removed.Add(outcome);
                freed += outcome.SizeBytes ?? 0;
            }
        }

        var failed = items.Where(item => item.Outcome == RetentionItemOutcomes.Failed).ToArray();
        await NotifyChangesAsync(rule, newlyMarked, removed, freed, ready, failed, cancellationToken);

        var held = marks.Values.Count(mark => mark.State == RetentionMarkStates.Held);
        var summary = Summary(newlyMarked.Count, items.Count(item => item.Outcome == RetentionItemOutcomes.Cleared), removed.Count, freed, ready.Count, held, failed.Length, marks.Count);
        return await FinishAsync(rule, RetentionRunKinds.Check, trigger, started, marks.Count, items, freed, summary, cancellationToken);
    }

    /// <summary>A person confirms Ready to remove titles. The rule is asked again first.</summary>
    public async Task<RetentionRun> ConfirmAsync(RetentionRule rule, IReadOnlyList<string> entityIds, CancellationToken cancellationToken)
    {
        var started = timeProvider.GetUtcNow();
        var marks = (await repository.ListMarksAsync(rule.Id, cancellationToken))
            .Where(mark => mark.State == RetentionMarkStates.Ready && entityIds.Contains(mark.EntityId, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        var items = new List<RetentionRunItem>();
        if (marks.Length == 0)
        {
            return await FinishAsync(rule, RetentionRunKinds.Confirmed, "person", started, 0, items, 0, "Nothing was ready to remove.", cancellationToken);
        }

        var evaluation = await evaluator.EvaluateAsync(rule, cancellationToken);
        if (evaluation.Problems.Count > 0 || evaluation.UnreachableServers.Count > 0)
        {
            var why = evaluation.Problems.Count > 0 ? string.Join(" ", evaluation.Problems) : string.Join("; ", evaluation.UnreachableServers);
            items.AddRange(marks.Select(mark => Item(mark, RetentionItemOutcomes.Held, $"Not removed: {why}.")));
            return await FinishAsync(rule, RetentionRunKinds.Confirmed, "person", started, marks.Length, items, 0, $"Nothing was removed: {why}.", cancellationToken);
        }

        var keeps = (await repository.ListKeepsAsync(cancellationToken))
            .Where(keep => keep.RuleId is null || string.Equals(keep.RuleId, rule.Id, StringComparison.OrdinalIgnoreCase))
            .Select(keep => keep.EntityId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = evaluation.Matched.ToDictionary(candidate => candidate.EntityId, StringComparer.OrdinalIgnoreCase);
        var removed = new List<RetentionRunItem>();
        long freed = 0;
        foreach (var mark in marks)
        {
            if (keeps.Contains(mark.EntityId) || !candidates.TryGetValue(mark.EntityId, out var candidate))
            {
                await repository.DeleteMarkAsync(rule.Id, mark.EntityId, cancellationToken);
                items.Add(Item(mark, RetentionItemOutcomes.Cleared, keeps.Contains(mark.EntityId) ? "Kept, so not removed." : "No longer matches the rule, so it was left alone."));
                continue;
            }

            var outcome = await RemoveAsync(rule, mark, candidate, started, cancellationToken);
            items.Add(outcome);
            if (outcome.Outcome == RetentionItemOutcomes.Removed)
            {
                removed.Add(outcome);
                freed += outcome.SizeBytes ?? 0;
            }
        }

        var failed = items.Where(item => item.Outcome == RetentionItemOutcomes.Failed).ToArray();
        await NotifyChangesAsync(rule, [], removed, freed, [], failed, cancellationToken);
        var left = items.Count - removed.Count - failed.Length;
        var summary = $"Removed {Titles(removed.Count)}, freeing {FormatBytes(freed)}"
                      + (failed.Length > 0 ? $"; {failed.Length} could not be removed" : string.Empty)
                      + (left > 0 ? $"; {left} left alone" : string.Empty)
                      + ".";
        return await FinishAsync(rule, RetentionRunKinds.Confirmed, "person", started, marks.Length - removed.Count - left, items, freed, summary, cancellationToken);
    }

    /// <summary>Keeps a title from one rule or all of them, and clears its marks there.</summary>
    /// <returns>Null when there is nothing to keep: no id, or a title Deluno has no name for.</returns>
    public async Task<RetentionKeepResult?> KeepAsync(SaveRetentionKeepRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.EntityId))
        {
            return null;
        }

        var entityId = request.EntityId.Trim();
        var marks = (await repository.ListMarksAsync(null, cancellationToken)).Where(mark => string.Equals(mark.EntityId, entityId, StringComparison.OrdinalIgnoreCase)).ToArray();
        var first = marks.FirstOrDefault();
        var title = first?.Title ?? request.Title?.Trim();
        var level = first?.Level ?? RetentionLevels.Normalize(request.Level, null);
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var ruleId = string.IsNullOrWhiteSpace(request.RuleId) ? null : request.RuleId.Trim();
        var keep = await repository.SaveKeepAsync(entityId, level, title, ruleId, request.Note, cancellationToken);
        var cleared = 0;
        foreach (var mark in marks.Where(mark => ruleId is null || string.Equals(mark.RuleId, ruleId, StringComparison.OrdinalIgnoreCase)))
        {
            await repository.DeleteMarkAsync(mark.RuleId, mark.EntityId, cancellationToken);
            cleared++;
        }

        await activity.RecordActivityAsync(
            "retention.kept",
            $"{title} was kept from {(ruleId is null ? "every retention rule" : $"retention rule “{keep.RuleName}”")}{(keep.Note is null ? "." : $": {keep.Note}")}",
            JsonSerializer.Serialize(new { keep.Id, EntityId = entityId, RuleId = ruleId }),
            null,
            level == RetentionLevels.Movie ? "movie" : "series",
            entityId,
            cancellationToken);
        return new RetentionKeepResult(keep, cleared);
    }

    /// <summary>Wants an episode a rule removed again: the record goes, and Deluno monitors it once more.</summary>
    public async Task<RetentionEpisodeRemoval?> UndoEpisodeRemovalAsync(string episodeId, CancellationToken cancellationToken)
    {
        var undone = await repository.DeleteEpisodeRemovalAsync(episodeId, cancellationToken);
        if (undone is null)
        {
            return null;
        }

        await series.UpdateEpisodeMonitoredAsync([episodeId], true, cancellationToken);
        await activity.RecordActivityAsync(
            "retention.undone",
            $"An episode removed by retention rule “{undone.RuleName}” is wanted again: season {undone.SeasonNumber}, episode {undone.EpisodeNumber}.",
            JsonSerializer.Serialize(new { undone.EpisodeId, undone.RuleId }),
            null,
            "series",
            undone.SeriesId,
            cancellationToken);
        return undone;
    }

    public async Task<RetentionTitleStatus> TitleStatusAsync(string entityId, CancellationToken cancellationToken)
    {
        var marks = await repository.ListMarksForTitleAsync(entityId, cancellationToken);
        var keeps = (await repository.ListKeepsAsync(cancellationToken))
            .Where(keep => string.Equals(keep.EntityId, entityId, StringComparison.OrdinalIgnoreCase) || keep.EntityId.StartsWith(entityId + ":", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return new RetentionTitleStatus(marks, keeps, await repository.ListEpisodeRemovalsAsync(entityId, cancellationToken));
    }

    // --------------------------------------------------------------- removing
    private async Task<RetentionRunItem> RemoveAsync(RetentionRule rule, RetentionMark mark, RetentionCandidate candidate, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var reason = $"removed by retention rule “{rule.Name}” on {Day(now)} ({string.Join("; ", candidate.Reasons)})";
        try
        {
            var serverNotes = RetentionLevels.IsPartOfShow(rule.Level)
                ? await RemovePartAsync(rule, candidate, reason, now, cancellationToken)
                : await RemoveTitleAsync(rule, candidate, reason, cancellationToken);
            if (serverNotes is null)
            {
                await repository.DeleteMarkAsync(rule.Id, mark.EntityId, cancellationToken);
                return Item(mark, RetentionItemOutcomes.Cleared, "Already gone from Deluno.");
            }

            await repository.DeleteMarksForEntityAsync(mark.EntityId, cancellationToken);
            if (!RetentionLevels.IsPartOfShow(rule.Level))
            {
                // A show that goes takes its seasons' and episodes' marks with it.
                foreach (var part in await repository.ListMarksForTitleAsync(mark.EntityId, cancellationToken))
                {
                    await repository.DeleteMarkAsync(part.RuleId, part.EntityId, cancellationToken);
                }
            }

            var message = RetentionLevels.IsPartOfShow(rule.Level)
                ? "Removed; the files are in the recycle bin and the episodes are no longer wanted."
                : "Removed; the files are in the recycle bin.";
            return Item(
                mark with { Reasons = candidate.Reasons, SizeBytes = candidate.SizeBytes },
                RetentionItemOutcomes.Removed,
                serverNotes.Count == 0 ? message : $"{message} {string.Join(" ", serverNotes)}");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Retention rule {Rule} could not remove {Title}.", rule.Name, mark.Title);
            return Item(mark, RetentionItemOutcomes.Failed, exception.Message);
        }
    }

    /// <returns>What each server said, or null when the title is already gone.</returns>
    private async Task<IReadOnlyList<string>?> RemoveTitleAsync(RetentionRule rule, RetentionCandidate candidate, string reason, CancellationToken cancellationToken)
    {
        (TitleToRemove Title, string? TmdbId)? target;
        if (rule.Level == RetentionLevels.Show)
        {
            var show = await series.GetByIdAsync(candidate.EntityId, cancellationToken);
            target = show is null ? null : (SeriesRemoval.Target(show, series), Tmdb(show.MetadataProvider, show.MetadataProviderId));
        }
        else
        {
            var movie = await movies.GetByIdAsync(candidate.EntityId, cancellationToken);
            target = movie is null ? null : (MovieRemoval.Target(movie, movies), Tmdb(movie.MetadataProvider, movie.MetadataProviderId));
        }

        if (target is null)
        {
            return null;
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

        return rule.RemoveFromServers
            ? await TellServersAsync(candidate.ServerItems.Select(match => (match.Server, match.Item)), candidate.Title, cancellationToken)
            : [];
    }

    /// <summary>A season's or episode's files go; the show stays, and the episodes are no longer wanted.</summary>
    private async Task<IReadOnlyList<string>?> RemovePartAsync(RetentionRule rule, RetentionCandidate candidate, string reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var unit = candidate.Unit;
        var episodeIds = (unit.Part?.EpisodeIds ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (unit.ShowId is null || episodeIds.Count == 0)
        {
            return null;
        }

        var allLibraries = await libraries.ListLibrariesAsync(cancellationToken);
        var files = new List<Deluno.Series.Contracts.SeriesTrackedFileItem>();
        foreach (var library in allLibraries.Where(library => library.MediaType == "tv"))
        {
            await foreach (var file in series.StreamTrackedFilesAsync(library.Id, cancellationToken))
            {
                if (string.Equals(file.SeriesId, unit.ShowId, StringComparison.OrdinalIgnoreCase) && file.EpisodeId is not null && episodeIds.Contains(file.EpisodeId))
                {
                    files.Add(file);
                }
            }
        }

        if (files.Count == 0)
        {
            return null;
        }

        await recycleBin.MoveAsync(files.Select(file => new TrackedLibraryFile(file.LibraryId, file.FilePath)).ToArray(), allLibraries, cancellationToken);
        foreach (var file in files)
        {
            await series.MarkTrackedFileMissingAsync(file.SeriesId, file.EpisodeId, file.LibraryId, file.FilePath, cancellationToken);
        }

        await series.UpdateEpisodeMonitoredAsync(episodeIds.ToArray(), false, cancellationToken);
        var sentence = char.ToUpperInvariant(reason[0]) + reason[1..];
        foreach (var file in files.DistinctBy(file => file.EpisodeId, StringComparer.OrdinalIgnoreCase))
        {
            await repository.SaveEpisodeRemovalAsync(
                new RetentionEpisodeRemoval(file.EpisodeId!, unit.ShowId, file.SeasonNumber, file.EpisodeNumber, rule.Id, rule.Name, sentence, now),
                cancellationToken);
        }

        await activity.RecordActivityAsync(
            "retention.removed",
            $"{unit.Title}: {sentence}.",
            JsonSerializer.Serialize(new { RuleId = rule.Id, EpisodeIds = episodeIds }),
            null,
            "series",
            unit.ShowId,
            cancellationToken);

        if (!rule.RemoveFromServers)
        {
            return [];
        }

        // A season is told episode by episode: those are the items that hold files.
        var paths = files.Select(file => RetentionServerIndex.NormalizePath(file.FilePath)).ToHashSet(StringComparer.Ordinal);
        var told = candidate.ServerItems.SelectMany(match => match.Episodes.Count > 0
            ? match.Episodes.Where(episode => episode.Paths.Any(path => paths.Contains(RetentionServerIndex.NormalizePath(path)))).Select(episode => (match.Server, Item: episode))
            : [(match.Server, match.Item)]);
        return await TellServersAsync(told, unit.Title, cancellationToken);
    }

    private async Task<IReadOnlyList<string>> TellServersAsync(
        IEnumerable<(Deluno.Connections.Contracts.MediaServerConnection Server, MediaServerItem Item)> items,
        string title,
        CancellationToken cancellationToken)
    {
        var notes = new List<string>();
        foreach (var group in items.GroupBy(entry => entry.Server.Id))
        {
            var server = group.First().Server;
            var client = clients.Find(server.Kind);
            string? problem = null;
            foreach (var (_, item) in group)
            {
                var answer = client is null
                    ? new MediaServerAnswer<MediaServerRemoval>.NotSupported($"{server.Name} cannot be updated by this build.")
                    : await client.RemoveAsync(server, item, cancellationToken);
                problem ??= answer switch
                {
                    MediaServerAnswer<MediaServerRemoval>.Unreachable unreachable => unreachable.Reason,
                    MediaServerAnswer<MediaServerRemoval>.NotSupported unsupported => unsupported.Reason,
                    _ => null
                };
            }

            notes.Add(problem is null
                ? $"{server.Name} was told {title} is gone."
                : $"{server.Name} was not updated: {problem.TrimEnd('.')}. It will drop the files on its next library scan.");
        }

        return notes;
    }

    // ---------------------------------------------------------------- telling
    private async Task NotifyChangesAsync(
        RetentionRule rule,
        IReadOnlyList<RetentionMark> marked,
        IReadOnlyList<RetentionRunItem> removed,
        long freed,
        IReadOnlyList<RetentionMark> ready,
        IReadOnlyList<RetentionRunItem> failed,
        CancellationToken cancellationToken)
    {
        if (marked.Count > 0)
        {
            await NotifyAsync(LeavingSoonEvent, $"{Titles(marked.Count)} leaving soon",
                $"Retention rule “{rule.Name}”: {string.Join("; ", marked.Take(20).Select(mark => $"{mark.Title} on {Day(mark.RemoveAfterUtc)}"))}{More(marked.Count)}.", rule, cancellationToken);
        }

        if (removed.Count > 0)
        {
            await NotifyAsync(RemovedEvent, $"{Titles(removed.Count)} removed, freeing {FormatBytes(freed)}",
                $"Retention rule “{rule.Name}”: {List(removed.Select(item => item.Title))}.", rule, cancellationToken);
        }

        if (ready.Count > 0)
        {
            await NotifyAsync(WaitingEvent, $"{Titles(ready.Count)} ready to remove",
                $"Retention rule “{rule.Name}” asks before removing: {List(ready.Select(mark => mark.Title))}.", rule, cancellationToken);
        }

        if (failed.Count > 0)
        {
            await NotifyAsync(FailedEvent, $"{Titles(failed.Count)} could not be removed",
                $"Retention rule “{rule.Name}”: {string.Join("; ", failed.Take(20).Select(item => $"{item.Title}: {item.Message}"))}{More(failed.Count)}.", rule, cancellationToken);
        }
    }

    private async Task NotifyAsync(string category, string title, string message, RetentionRule rule, CancellationToken cancellationToken)
    {
        try
        {
            await notifications.DispatchAsync(category, title, message, JsonSerializer.Serialize(new { RuleId = rule.Id, RuleName = rule.Name }), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "The retention notification {Category} could not be sent.", category);
        }
    }

    private async Task<RetentionRun> FinishAsync(
        RetentionRule rule,
        string kind,
        string trigger,
        DateTimeOffset started,
        int leaving,
        IReadOnlyList<RetentionRunItem> items,
        long freed,
        string summary,
        CancellationToken cancellationToken)
    {
        var run = new RetentionRun(
            Guid.CreateVersion7().ToString("N"), rule.Id, rule.Name, kind, trigger, null,
            started, timeProvider.GetUtcNow(),
            leaving,
            items.Count(item => item.Outcome is RetentionItemOutcomes.Held or RetentionItemOutcomes.Undecided),
            items.Count(item => item.Outcome == RetentionItemOutcomes.Removed),
            items.Count(item => item.Outcome == RetentionItemOutcomes.Failed),
            freed,
            summary,
            items);
        await repository.SaveRunAsync(run, cancellationToken);
        // A scheduled check that changed nothing is history, not news.
        if (items.Count > 0 || trigger != "schedule")
        {
            await activity.RecordActivityAsync(
                "retention.check",
                $"Retention rule “{rule.Name}”: {summary}",
                JsonSerializer.Serialize(new { RuleId = rule.Id, RunId = run.Id, Trigger = trigger }),
                null,
                "retention-rule",
                rule.Id,
                cancellationToken);
        }

        return run;
    }

    private static string Summary(int marked, int cleared, int removed, long freed, int ready, int held, int failed, int leaving)
    {
        var parts = new List<string>();
        if (marked > 0) parts.Add($"{Titles(marked)} marked leaving soon");
        if (cleared > 0) parts.Add($"{Titles(cleared)} no longer leaving");
        if (removed > 0) parts.Add($"{Titles(removed)} removed, freeing {FormatBytes(freed)}");
        if (ready > 0) parts.Add($"{Titles(ready)} ready to remove");
        if (held > 0) parts.Add($"{Titles(held)} held");
        if (failed > 0) parts.Add($"{Titles(failed)} could not be removed");
        // The total is said only when it adds something: "1 title marked
        // leaving soon. 1 title leaving soon." told the reader the same thing twice.
        if (parts.Count == 0)
        {
            return leaving == 0 ? "Nothing changed; nothing is leaving soon." : $"Nothing changed; {Titles(leaving)} still leaving soon.";
        }

        var said = string.Join("; ", parts);
        var total = leaving == marked ? string.Empty : leaving == 0 ? " Nothing else is leaving soon." : $" {Titles(leaving)} leaving soon in all.";
        return $"{char.ToUpperInvariant(said[0])}{said[1..]}.{total}";
    }

    private static RetentionRunItem Item(RetentionMark mark, string outcome, string? message)
        => new(mark.EntityId, mark.MediaType, mark.Title, mark.Year, mark.SizeBytes, mark.Reasons, outcome, message);

    private static string Day(DateTimeOffset when) => when.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    private static string List(IEnumerable<string> titles)
    {
        var all = titles.ToArray();
        return string.Join(", ", all.Take(20)) + More(all.Length);
    }

    private static string More(int count) => count > 20 ? $" and {count - 20} more" : string.Empty;

    private static string? Tmdb(string? provider, string? id)
        => string.Equals(provider, "tmdb", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(id) ? id : null;

    /// <summary>The key an import list entry is looked up by, so a list finds the exclusion.</summary>
    internal static string EntryKey(string title, int? year, string? imdbId)
        => !string.IsNullOrWhiteSpace(imdbId)
            ? $"imdb:{imdbId.Trim().ToLowerInvariant()}"
            : $"title:{title.Trim().ToLowerInvariant()}:{year?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}";

    private static string Titles(int count) => $"{count} {(count == 1 ? "title" : "titles")}";

    internal static string FormatBytes(long bytes)
        => bytes >= 1L << 30
            ? $"{bytes / (double)(1L << 30):0.#} GB"
            : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0.#} MB" : $"{bytes / 1024.0:0.#} KB";
}
