using System.Globalization;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Connections.Data;
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
    IRetentionSeerr seerr,
    IActivityFeedRepository activity,
    IOutboundNotificationService notifications,
    TimeProvider timeProvider,
    ILogger<RetentionService> logger,
    // #856: gates the per-file "file.deleted" event a season/episode removal
    // fires below, same toggle a title removal or a manual Files-tab delete
    // uses. Optional so existing positional test constructions keep compiling.
    INotificationService? notificationPreferences = null,
    // #990: the show's own tags, so a tagged notification connection still
    // fires for a season/episode removal scoped to it.
    IMediaTagStore? mediaTagStore = null,
    // #1011: the "tag" expiry action gives the title a Deluno tag.
    ITitleTagService? titleTags = null,
    // #1012: every server a rule reads is asked what is being played before a removal.
    IMediaServerRepository? mediaServers = null,
    // #1365: a season or episode removal tells the media servers its files are gone, as a title removal does.
    Deluno.Jobs.Data.IJobScheduler? mediaServerJobScheduler = null)
{
    public const string LeavingSoonEvent = "retention.leaving-soon";
    public const string RemovedEvent = "retention.removed";
    /// <summary>A scheduled removal is stuck because a media server could not be reached (#833).</summary>
    public const string WaitingUnreachableEvent = "retention.waiting-unreachable";
    /// <summary>A title is Ready to remove and is simply awaiting a person's confirmation (#833).</summary>
    public const string WaitingConfirmationEvent = "retention.waiting-confirmation";
    public const string FailedEvent = "retention.failed";

    // One at a time per rule: a scheduled check, "Run now" and a confirmation must not act on the same
    // marks together, or an action (unmonitor, tag) would be done and written to Activity twice (#1072).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> RuleGates = new(StringComparer.OrdinalIgnoreCase);

    private static async Task<T> OneAtATimeAsync<T>(string ruleId, Func<Task<T>> work, CancellationToken cancellationToken)
    {
        var gate = RuleGates.GetOrAdd(ruleId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await work();
        }
        finally
        {
            gate.Release();
        }
    }

    // A save of a rule (and the restart of its waits) takes this short gate; a run takes it only for the short steps that
    // read or change the rule's marks or remove one title, never while it reads the media servers. So saving or switching
    // off a rule waits for at most one removal, never for a whole run (#1072).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> EditGates = new(StringComparer.OrdinalIgnoreCase);

    private sealed class EditHold(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }

    private static async Task<EditHold> HoldEditAsync(string ruleId, CancellationToken cancellationToken)
    {
        var gate = EditGates.GetOrAdd(ruleId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        return new EditHold(gate);
    }

    private static async Task<T> EditingAsync<T>(string ruleId, Func<Task<T>> work, CancellationToken cancellationToken)
    {
        using var hold = await HoldEditAsync(ruleId, cancellationToken);
        return await work();
    }

    /// <summary>Whether the rule was edited, switched off (a scheduled check) or deleted since 'before' was read.</summary>
    private async Task<bool> RuleChangedAsync(RetentionRule before, string trigger, CancellationToken cancellationToken)
    {
        var current = await repository.GetRuleAsync(before.Id, cancellationToken);
        return current is null
            || (trigger == "schedule" && !current.IsEnabled)
            || current.UpdatedUtc != before.UpdatedUtc
            || current.ExpiryAction != before.ExpiryAction
            || !string.Equals(current.ExpiryTag, before.ExpiryTag, StringComparison.OrdinalIgnoreCase)
            || current.RemovalMode != before.RemovalMode
            || current.Level != before.Level
            || current.WaitDays != before.WaitDays;
    }

    private const string EditedWhileRunning = "The rule was edited or switched off while this ran, so it stopped before changing anything more; the next check uses the new settings.";

    public Task<RetentionRun> CheckAsync(RetentionRule rule, string trigger, CancellationToken cancellationToken)
        => OneAtATimeAsync(rule.Id, async () =>
        {
            // A check can sit queued behind another for minutes with the rule as it was when it was listed. The rule
            // is read again here, inside the gate, so an edit made in that time (a new action, a protecting step,
            // switched off, deleted) is what the check obeys (#1072).
            var current = await repository.GetRuleAsync(rule.Id, cancellationToken);
            if (current is null)
            {
                return NotRun(rule, RetentionRunKinds.Check, trigger, "The rule no longer exists, so nothing was checked.");
            }

            if (trigger == "schedule" && !current.IsEnabled)
            {
                return NotRun(current, RetentionRunKinds.Check, trigger, "The rule was switched off while this check waited, so it was not run.");
            }

            return await CheckCoreAsync(current, trigger, cancellationToken);
        }, cancellationToken);

    /// <summary>An answer for a check or confirmation that did not run; nothing is written to history.</summary>
    private RetentionRun NotRun(RetentionRule rule, string kind, string trigger, string summary)
    {
        var now = timeProvider.GetUtcNow();
        return new RetentionRun(Guid.CreateVersion7().ToString("N"), rule.Id, rule.Name, kind, trigger, null, now, now, 0, 0, 0, 0, 0, summary, []);
    }

    private async Task<RetentionRun> CheckCoreAsync(RetentionRule rule, string trigger, CancellationToken cancellationToken)
    {
        var started = timeProvider.GetUtcNow();
        Dictionary<string, RetentionMark> marks;
        using (await HoldEditAsync(rule.Id, cancellationToken))
        {
            await repository.MarkCheckedAsync(rule.Id, started, cancellationToken);
            marks = (await repository.ListMarksAsync(rule.Id, cancellationToken)).ToDictionary(mark => mark.EntityId, StringComparer.OrdinalIgnoreCase);
        }

        // Titles a person put on the list by hand are asked for by name: the steps do not decide them (#1008).
        var manualIds = marks.Values.Where(mark => mark.Manual).Select(mark => mark.EntityId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // The media servers are read without holding anything, so saving the rule never waits for that.
        var evaluation = await evaluator.EvaluateAsync(rule, cancellationToken, null, manualIds);
        var items = new List<RetentionRunItem>();

        // What follows changes the rule's marks, so the rule is read again first: if it was edited, switched off or
        // deleted while the servers were read, nothing is changed and the next check uses the new settings.
        using var editing = await HoldEditAsync(rule.Id, cancellationToken);
        if (await RuleChangedAsync(rule, trigger, cancellationToken))
        {
            return NotRun(rule, RetentionRunKinds.Check, trigger, EditedWhileRunning);
        }
        marks = (await repository.ListMarksAsync(rule.Id, cancellationToken)).ToDictionary(mark => mark.EntityId, StringComparer.OrdinalIgnoreCase);

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
            var due = marks.Values.Where(mark => mark.RemoveAfterUtc <= started && mark.State != RetentionMarkStates.Done).ToArray();
            foreach (var mark in due)
            {
                await repository.UpsertMarkAsync(mark with { State = RetentionMarkStates.Held, StateReason = why, UpdatedUtc = started }, cancellationToken);
                items.Add(Item(mark, RetentionItemOutcomes.Held, $"Not removed: {why}."));
            }

            if (due.Length > 0)
            {
                await NotifyAsync(WaitingUnreachableEvent, $"{Titles(due.Length)} could not be removed yet", $"Retention rule “{rule.Name}”: {why}. {List(due.Select(mark => mark.Title))}.", rule, cancellationToken);
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
            if (candidate?.Outcome == RetentionItemOutcomes.Undecided && mark.State == RetentionMarkStates.Done)
            {
                // Its day came and the rule's action was done; not knowing changes nothing, and it is not done twice.
                continue;
            }

            if (candidate?.Outcome == RetentionItemOutcomes.Undecided)
            {
                var holding = mark with { State = RetentionMarkStates.Held, StateReason = candidate.Message, UpdatedUtc = started };
                await repository.UpsertMarkAsync(holding, cancellationToken);
                marks[mark.EntityId] = holding;
                if (mark.State != RetentionMarkStates.Held)
                {
                    // Said once, when it starts being held, so Activity shows it and a steady hold is not repeated every check.
                    items.Add(Item(mark, RetentionItemOutcomes.Held, $"Held: {candidate.Message}"));
                }

                continue;
            }

            if (candidate?.Outcome != RetentionItemOutcomes.Matched)
            {
                await repository.DeleteMarkAsync(rule.Id, mark.EntityId, cancellationToken);
                marks.Remove(mark.EntityId);
                items.Add(Item(mark, RetentionItemOutcomes.Cleared, mark.Manual ? "It is no longer in Deluno, so it is off the list." : "No longer matches the rule."));
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
                    ServerItems = ServerItems(candidate),
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

            var mark = BuildMark(rule, candidate, started, manual: false);
            await repository.UpsertMarkAsync(mark, cancellationToken);
            marks[candidate.EntityId] = mark;
            newlyMarked.Add(mark);
            items.Add(Item(mark, RetentionItemOutcomes.Marked, $"Leaving soon: {LeavingSentence(rule, mark)} unless it stops matching or is kept."));
        }

        // Due: the day has come, and this check has just confirmed it still matches.
        var removed = new List<RetentionRunItem>();
        var ready = new List<RetentionMark>();
        long freed = 0;
        PlaybackReading? playback = null;
        var deferred = new List<RetentionMark>();
        var dueMarks = marks.Values
            .Where(mark => mark.RemoveAfterUtc <= started && mark.State is not (RetentionMarkStates.Held or RetentionMarkStates.Done))
            .ToArray();
        string? stoppedEarly = null;
        editing.Dispose();
        foreach (var mark in dueMarks)
        {
            // One title at a time: a waiting save gets in between two, and the rule is read again before each, so
            // nothing is removed under a rule that was edited or switched off a moment ago.
            using var oneTitle = await HoldEditAsync(rule.Id, cancellationToken);
            if (await RuleChangedAsync(rule, trigger, cancellationToken))
            {
                stoppedEarly = EditedWhileRunning;
                break;
            }

            if (rule.RemovalMode == RetentionRemovalModes.Ask)
            {
                if (mark.State != RetentionMarkStates.Ready)
                {
                    await repository.UpsertMarkAsync(mark with { State = RetentionMarkStates.Ready, UpdatedUtc = started }, cancellationToken);
                    ready.Add(mark);
                    items.Add(Item(mark, RetentionItemOutcomes.Ready, rule.ExpiryAction == RetentionExpiryActions.Delete ? "Ready to remove: waiting for you to confirm." : $"Ready: waiting for you to confirm. {RetentionExpiryActions.Describe(rule.ExpiryAction, rule.ExpiryTag)}"));
                }

                continue;
            }

            // Never take something out from under a viewer, and never guess that nobody is watching (#1012).
            if (NeedsPlaybackCheck(rule))
            {
                // A fresh reading immediately before EACH removal: a play can start between two deletions in one run (#1071).
                playback = await ReadPlaybackAsync(cancellationToken);
                if (PlaybackHold(playback, candidates[mark.EntityId]) is { } hold)
                {
                    var holding = mark with { State = RetentionMarkStates.Held, StateReason = hold, UpdatedUtc = started };
                    await repository.UpsertMarkAsync(holding, cancellationToken);
                    marks[mark.EntityId] = holding;
                    items.Add(Item(mark, RetentionItemOutcomes.Held, hold));
                    deferred.Add(mark);
                    continue;
                }
            }

            var outcome = await ActAsync(rule, mark, candidates[mark.EntityId], started, cancellationToken);
            items.Add(outcome);
            if (outcome.Outcome is RetentionItemOutcomes.Removed or RetentionItemOutcomes.Cleared)
            {
                marks.Remove(mark.EntityId);
            }
            else if (outcome.Outcome == RetentionItemOutcomes.Done)
            {
                marks[mark.EntityId] = mark with { State = RetentionMarkStates.Done, StateReason = outcome.Message, UpdatedUtc = started };
            }

            if (outcome.Outcome == RetentionItemOutcomes.Removed)
            {
                removed.Add(outcome);
                freed += outcome.SizeBytes ?? 0;
            }
        }

        await TellDeferredAsync(rule, deferred, playback, cancellationToken);
        var failed = items.Where(item => item.Outcome == RetentionItemOutcomes.Failed).ToArray();
        await NotifyChangesAsync(rule, newlyMarked, removed, freed, ready, failed, cancellationToken);

        var held = marks.Values.Count(mark => mark.State == RetentionMarkStates.Held);
        var done = items.Where(item => item.Outcome == RetentionItemOutcomes.Done).ToArray();
        var leaving = marks.Values.Count(mark => mark.State != RetentionMarkStates.Done);
        var summary = Summary(newlyMarked.Count, items.Count(item => item.Outcome == RetentionItemOutcomes.Cleared), removed.Count, freed, ready.Count, held, failed.Length, leaving, done.Length > 0 ? DoneSentence(rule, done.Length) : null, deferred.Count, evaluation.UpcomingLeftAlone);
        if (stoppedEarly is not null)
        {
            summary = $"{summary} {stoppedEarly}";
        }

        return await FinishAsync(rule, RetentionRunKinds.Check, trigger, started, leaving, items, freed, summary, cancellationToken);
    }

    /// <summary>A person confirms Ready to remove titles. The rule is asked again first.</summary>
    public Task<RetentionRun> ConfirmAsync(RetentionRule rule, IReadOnlyList<string> entityIds, CancellationToken cancellationToken)
        => OneAtATimeAsync(rule.Id, async () =>
        {
            var current = await repository.GetRuleAsync(rule.Id, cancellationToken);
            return current is null
                ? NotRun(rule, RetentionRunKinds.Confirmed, "person", "The rule no longer exists, so nothing was removed.")
                : await ConfirmCoreAsync(current, entityIds, cancellationToken);
        }, cancellationToken);

    private async Task<RetentionRun> ConfirmCoreAsync(RetentionRule rule, IReadOnlyList<string> entityIds, CancellationToken cancellationToken)
    {
        var started = timeProvider.GetUtcNow();
        var allMarks = await repository.ListMarksAsync(rule.Id, cancellationToken);
        // Titles a person added by hand are asked for by name, as CheckAsync does (#1069); without
        // them a hand-added title fell out of the matches and its mark was cleared on confirm.
        var manualIds = allMarks.Where(mark => mark.Manual).Select(mark => mark.EntityId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var marks = allMarks
            .Where(mark => mark.State == RetentionMarkStates.Ready && entityIds.Contains(mark.EntityId, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        var items = new List<RetentionRunItem>();
        if (marks.Length == 0)
        {
            return await FinishAsync(rule, RetentionRunKinds.Confirmed, "person", started, 0, items, 0, "Nothing was ready to remove.", cancellationToken);
        }

        var evaluation = await evaluator.EvaluateAsync(rule, cancellationToken, null, manualIds);
        using var editing = await HoldEditAsync(rule.Id, cancellationToken);
        if (await RuleChangedAsync(rule, "person", cancellationToken))
        {
            return NotRun(rule, RetentionRunKinds.Confirmed, "person", "The rule was edited while this was being confirmed, so nothing was removed. Look at the list again.");
        }

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
        PlaybackReading? playback = null;
        var deferred = new List<RetentionMark>();
        var actedCount = 0;
        string? stopped = null;
        editing.Dispose();
        foreach (var mark in marks)
        {
            using var oneTitle = await HoldEditAsync(rule.Id, cancellationToken);
            if (await RuleChangedAsync(rule, "person", cancellationToken))
            {
                stopped = EditedWhileRunning;
                break;
            }

            if (keeps.Contains(mark.EntityId) || !candidates.TryGetValue(mark.EntityId, out var candidate))
            {
                await repository.DeleteMarkAsync(rule.Id, mark.EntityId, cancellationToken);
                items.Add(Item(mark, RetentionItemOutcomes.Cleared, keeps.Contains(mark.EntityId) ? "Kept, so not removed." : "No longer matches the rule, so it was left alone."));
                continue;
            }

            if (NeedsPlaybackCheck(rule))
            {
                // A fresh reading immediately before EACH removal: a play can start between two deletions in one run (#1071).
                playback = await ReadPlaybackAsync(cancellationToken);
                if (PlaybackHold(playback, candidate) is { } hold)
                {
                    // Stays Ready to remove: the person can confirm again once it has stopped.
                    items.Add(Item(mark, RetentionItemOutcomes.Held, hold));
                    deferred.Add(mark);
                    continue;
                }
            }

            var outcome = await ActAsync(rule, mark, candidate, started, cancellationToken);
            items.Add(outcome);
            if (outcome.Outcome == RetentionItemOutcomes.Removed)
            {
                removed.Add(outcome);
                freed += outcome.SizeBytes ?? 0;
            }
            else if (outcome.Outcome == RetentionItemOutcomes.Done)
            {
                actedCount++;
            }
        }

        await TellDeferredAsync(rule, deferred, playback, cancellationToken);
        var failed = items.Where(item => item.Outcome == RetentionItemOutcomes.Failed).ToArray();
        await NotifyChangesAsync(rule, [], removed, freed, [], failed, cancellationToken);
        var left = items.Count - removed.Count - failed.Length - actedCount;
        var summary = (rule.ExpiryAction == RetentionExpiryActions.Delete || removed.Count > 0
                          ? $"Removed {Titles(removed.Count)}, freeing {FormatBytes(freed)}"
                          : $"{char.ToUpperInvariant(DoneSentence(rule, actedCount)[0])}{DoneSentence(rule, actedCount)[1..]}")
                      + (failed.Length > 0 ? $"; {failed.Length} could not be removed" : string.Empty)
                      + (deferred.Count > 0 ? $"; {Titles(deferred.Count)} left alone while being played, or while what is being played could not be seen" : string.Empty)
                      + (left - deferred.Count > 0 ? $"; {left - deferred.Count} left alone" : string.Empty)
                      + ".";
        if (stopped is not null)
        {
            summary = $"{summary} {stopped}";
        }

        return await FinishAsync(rule, RetentionRunKinds.Confirmed, "person", started, marks.Length - removed.Count - left - actedCount, items, freed, summary, cancellationToken);
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

    // ------------------------------------------------------------ by hand (#1008, #1010)

    /// <summary>
    /// Puts a title on a rule's Leaving soon list by hand. It is marked MANUAL, waits
    /// the rule's days like any other, and the rule's steps no longer decide whether it
    /// stays: only a keep, or the title leaving Deluno, takes it off. Taking a title off
    /// by hand is a keep from this rule, so the rule cannot put it back.
    /// </summary>
    public async Task<RetentionManualAddResult> AddManualAsync(RetentionRule rule, string? entityId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(entityId))
        {
            return new RetentionManualAddResult(null, "Choose a title to add.");
        }

        var id = entityId.Trim();
        if (!rule.IsEnabled)
        {
            return new RetentionManualAddResult(null, "Turn the rule on first, so its list is checked.");
        }

        var kept = (await repository.ListKeepsAsync(cancellationToken)).Any(keep =>
            string.Equals(keep.EntityId, id, StringComparison.OrdinalIgnoreCase) &&
            (keep.RuleId is null || string.Equals(keep.RuleId, rule.Id, StringComparison.OrdinalIgnoreCase)));
        if (kept)
        {
            return new RetentionManualAddResult(null, "It is kept, so it cannot be added. Undo the keep first.");
        }

        var evaluation = await evaluator.EvaluateAsync(rule, cancellationToken, null, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { id });
        if (evaluation.Problems.Count > 0)
        {
            return new RetentionManualAddResult(null, string.Join(" ", evaluation.Problems));
        }

        if (evaluation.UnreachableServers.Count > 0)
        {
            return new RetentionManualAddResult(null, $"Not added: {string.Join("; ", evaluation.UnreachableServers.Select(reason => reason.TrimEnd('.')))}.");
        }

        var candidate = evaluation.Candidates.FirstOrDefault(item => string.Equals(item.EntityId, id, StringComparison.OrdinalIgnoreCase) && item.Outcome == RetentionItemOutcomes.Matched);
        if (candidate is null)
        {
            return new RetentionManualAddResult(null, "Deluno has no title like that with files for this rule to act on.");
        }

        var now = timeProvider.GetUtcNow();
        var existing = (await repository.ListMarksAsync(rule.Id, cancellationToken)).FirstOrDefault(mark => string.Equals(mark.EntityId, id, StringComparison.OrdinalIgnoreCase));
        var mark = existing is null
            ? BuildMark(rule, candidate, now, manual: true)
            : existing with { Manual = true, ServerItems = ServerItems(candidate), UpdatedUtc = now };
        await repository.UpsertMarkAsync(mark, cancellationToken);

        var (entityType, entityKey) = ActivityEntity(mark);
        await activity.RecordActivityAsync(
            "retention.added-by-hand",
            existing is null
                ? $"{mark.Title} was added by hand to retention rule “{rule.Name}”: {DayOf(rule, mark)}, unless it is kept."
                : $"{mark.Title} was marked as added by hand on retention rule “{rule.Name}”, so the rule no longer decides whether it stays.",
            JsonSerializer.Serialize(new { RuleId = rule.Id, EntityId = id }),
            null,
            entityType,
            entityKey,
            cancellationToken);
        return new RetentionManualAddResult(mark, null);
    }

    /// <summary>
    /// Pushes each title's day back by <paramref name="days"/> without keeping it (#1010). A title already
    /// past its day counts from today. It only ever moves a day later, so it can never make a removal happen sooner.
    /// </summary>
    public async Task<IReadOnlyList<RetentionMark>> PostponeAsync(RetentionRule rule, IReadOnlyList<string> entityIds, int days, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var wanted = entityIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var moved = new List<RetentionMark>();
        foreach (var mark in (await repository.ListMarksAsync(rule.Id, cancellationToken)).Where(mark => wanted.Contains(mark.EntityId) && mark.State != RetentionMarkStates.Done))
        {
            var from = mark.RemoveAfterUtc > now ? mark.RemoveAfterUtc : now;
            var next = mark with { RemoveAfterUtc = from.AddDays(days), State = RetentionMarkStates.Leaving, StateReason = null, UpdatedUtc = now };
            await repository.UpsertMarkAsync(next, cancellationToken);
            moved.Add(next);
            var (entityType, entityKey) = ActivityEntity(next);
            await activity.RecordActivityAsync(
                "retention.postponed",
                $"{mark.Title} was postponed by {days} {(days == 1 ? "day" : "days")} on retention rule “{rule.Name}”: {DayOf(rule, next)}. It is not kept, so it still goes then if it still matches.",
                JsonSerializer.Serialize(new { RuleId = rule.Id, mark.EntityId, Days = days }),
                null,
                entityType,
                entityKey,
                cancellationToken);
        }

        return moved;
    }

    /// <summary>
    /// A rule's action was switched to delete: marks made under the old action (log, tag, unmonitor) must
    /// not remove anything on the day they were promised for. Their wait starts again from now, and a mark
    /// already Ready (confirmed for the old action) goes back to waiting, so nothing is ever removed early (#1072).
    /// </summary>
    public Task<int> RestartWaitForDeleteAsync(RetentionRule rule, CancellationToken cancellationToken)
        => EditingAsync(rule.Id, () => RestartWaitCoreAsync(rule, cancellationToken), cancellationToken);

    /// <summary>
    /// Saves an edited rule under the rule's own gate, and, when the edit switches its action to delete, restarts
    /// the wait before the gate opens again, so a check can never run under Delete against marks whose day was
    /// promised for the old action (#1072). Null when the rule does not exist.
    /// </summary>
    public Task<RetentionRule?> SaveEditedRuleAsync(string id, SaveRetentionRuleRequest request, CancellationToken cancellationToken)
        => EditingAsync(id, async () =>
        {
            var before = await repository.GetRuleAsync(id, cancellationToken);
            var saved = await repository.SaveRuleAsync(id, request, cancellationToken);
            if (saved is null)
            {
                return null;
            }

            // A titles whose day already came under the old action is done afresh, after a new wait, under the new one.
            if (before is not null && (before.ExpiryAction != saved.ExpiryAction || !string.Equals(before.ExpiryTag, saved.ExpiryTag, StringComparison.OrdinalIgnoreCase)))
            {
                await ForgetDoneAsync(saved.Id, cancellationToken);
            }

            // Marks made while it only logged, tagged or unmonitored were promised a day for that, not for deleting.
            if (before is not null && saved.ExpiryAction == RetentionExpiryActions.Delete && before.ExpiryAction != RetentionExpiryActions.Delete)
            {
                await RestartWaitCoreAsync(saved, cancellationToken);
            }

            return saved;
        }, cancellationToken);

    private async Task<int> RestartWaitCoreAsync(RetentionRule rule, CancellationToken cancellationToken)
    {
        {
            var now = timeProvider.GetUtcNow();
            var restarted = 0;
            foreach (var mark in (await repository.ListMarksAsync(rule.Id, cancellationToken)).Where(mark => mark.State != RetentionMarkStates.Done))
            {
                var next = mark with
                {
                    RemoveAfterUtc = now.AddDays(rule.WaitDays),
                    State = mark.State == RetentionMarkStates.Held ? RetentionMarkStates.Held : RetentionMarkStates.Leaving,
                    UpdatedUtc = now
                };
                await repository.UpsertMarkAsync(next, cancellationToken);
                restarted++;
            }

            if (restarted > 0)
            {
                await activity.RecordActivityAsync(
                    "retention.restarted",
                    $"Retention rule “{rule.Name}” now deletes, so the wait for {Titles(restarted)} already leaving soon starts again from today: nothing is removed before {Day(now.AddDays(rule.WaitDays))}.",
                    JsonSerializer.Serialize(new { RuleId = rule.Id, Count = restarted }),
                    null,
                    "retention-rule",
                    rule.Id,
                    cancellationToken);
            }

            return restarted;
        }
    }

    /// <summary>Forgets what a rule did on a title's day, so a changed action is done afresh, after a new wait.</summary>
    public async Task ForgetDoneAsync(string ruleId, CancellationToken cancellationToken)
    {
        foreach (var mark in (await repository.ListMarksAsync(ruleId, cancellationToken)).Where(mark => mark.State == RetentionMarkStates.Done))
        {
            await repository.DeleteMarkAsync(mark.RuleId, mark.EntityId, cancellationToken);
        }
    }

    private static RetentionMark BuildMark(RetentionRule rule, RetentionCandidate candidate, DateTimeOffset now, bool manual)
        => new(
            rule.Id, rule.Name, candidate.EntityId, rule.Level, candidate.Unit.ShowId, candidate.Unit.SeasonNumber,
            candidate.Unit.Part?.EpisodeIds ?? [], candidate.Title, candidate.Year, candidate.SizeBytes, candidate.Reasons,
            now, now.AddDays(rule.WaitDays), RetentionMarkStates.Leaving, null, now)
        {
            ServerItems = ServerItems(candidate),
            Manual = manual
        };

    private static (string Type, string Id) ActivityEntity(RetentionMark mark)
        => (mark.Level == RetentionLevels.Movie ? "movie" : "series", mark.ShowId ?? mark.EntityId);

    /// <summary>"goes on 3 Oct 2026", or what the rule does on its day instead of going.</summary>
    private static string DayOf(RetentionRule rule, RetentionMark mark)
        => rule.ExpiryAction == RetentionExpiryActions.Delete ? $"it goes on {Day(mark.RemoveAfterUtc)}" : $"its day is {Day(mark.RemoveAfterUtc)}";

    private static string LeavingSentence(RetentionRule rule, RetentionMark mark)
        => rule.ExpiryAction switch
        {
            RetentionExpiryActions.Unmonitor => $"stops being monitored on {Day(mark.RemoveAfterUtc)} (its files stay)",
            RetentionExpiryActions.Log => $"its day is {Day(mark.RemoveAfterUtc)}, and Deluno only writes it down",
            RetentionExpiryActions.Tag => $"gets the tag “{rule.ExpiryTag}” on {Day(mark.RemoveAfterUtc)}",
            _ => $"goes on {Day(mark.RemoveAfterUtc)}"
        };

    private static string DoneSentence(RetentionRule rule, int count)
        => rule.ExpiryAction switch
        {
            RetentionExpiryActions.Unmonitor => $"{Titles(count)} stopped being monitored (files kept)",
            RetentionExpiryActions.Log => $"{Titles(count)} reached {(count == 1 ? "its" : "their")} day and {(count == 1 ? "was" : "were")} only recorded",
            RetentionExpiryActions.Tag => $"{Titles(count)} tagged “{rule.ExpiryTag}”",
            _ => $"{Titles(count)} removed"
        };

    // ------------------------------------------------------- playing now (#1012)

    private sealed record PlaybackReading(IReadOnlyDictionary<string, IReadOnlyList<MediaServerPlay>> PlaysByServer, string? CouldNotSee);

    /// <summary>Only a removal is held back for playback; stopping monitoring, tagging and writing it down disturb nobody.</summary>
    private static bool NeedsPlaybackCheck(RetentionRule rule)
        => rule.DeferWhilePlaying && rule.ExpiryAction == RetentionExpiryActions.Delete;

    /// <summary>
    /// Asks EVERY enabled media server what is being played, not only the ones the rule reads: the files are
    /// deleted for all of them. Not being able to ask is recorded, never read as "nothing".
    /// </summary>
    private async Task<PlaybackReading> ReadPlaybackAsync(CancellationToken cancellationToken)
    {
        var plays = new Dictionary<string, IReadOnlyList<MediaServerPlay>>(StringComparer.OrdinalIgnoreCase);
        var problems = new List<string>();
        if (mediaServers is null)
        {
            return new PlaybackReading(plays, "Deluno could not list its media servers");
        }

        foreach (var server in await mediaServers.ListAsync(cancellationToken))
        {
            // A server that is turned off is not a server anyone is watching.
            if (!server.IsEnabled)
            {
                continue;
            }

            if (clients.Find(server.Kind) is not IMediaServerPlaybackReader reader)
            {
                problems.Add($"{server.Name} cannot say what is being played");
                continue;
            }

            MediaServerAnswer<IReadOnlyList<MediaServerPlay>> answer;
            try
            {
                answer = await reader.ListPlayingAsync(server, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Retention could not ask {Server} what is being played.", server.Name);
                answer = new MediaServerAnswer<IReadOnlyList<MediaServerPlay>>.Unreachable($"Deluno could not ask {server.Name} what is being played.");
            }

            switch (answer)
            {
                case MediaServerAnswer<IReadOnlyList<MediaServerPlay>>.Reported reported:
                    plays[server.Id] = reported.Value;
                    break;
                case MediaServerAnswer<IReadOnlyList<MediaServerPlay>>.Unreachable unreachable:
                    problems.Add(unreachable.Reason.TrimEnd('.'));
                    break;
                case MediaServerAnswer<IReadOnlyList<MediaServerPlay>>.NotSupported unsupported:
                    problems.Add(unsupported.Reason.TrimEnd('.'));
                    break;
            }
        }

        return new PlaybackReading(plays, problems.Count == 0 ? null : string.Join("; ", problems));
    }

    /// <returns>Why the title must wait, or null when nothing is playing and nothing is unknown.</returns>
    private static string? PlaybackHold(PlaybackReading reading, RetentionCandidate candidate)
    {
        if (reading.CouldNotSee is not null)
        {
            return $"Not removed: Deluno could not see what is being played ({reading.CouldNotSee}). It tries again at the next check.";
        }

        // On a server the rule reads, the title is known by that server's own id.
        var byItem = candidate.ServerItems.Any(match =>
            reading.PlaysByServer.TryGetValue(match.Server.Id, out var plays) &&
            plays.Any(play => play.ItemIds.Contains(match.Item.ServerItemId, StringComparer.OrdinalIgnoreCase) ||
                              match.Episodes.Any(episode => play.ItemIds.Contains(episode.ServerItemId, StringComparer.OrdinalIgnoreCase))));

        // On any server, by the file being played (the files are what is deleted), or for a film by its provider ids.
        var unit = candidate.Unit;
        var files = unit.Paths.Select(RetentionServerIndex.NormalizePath).ToHashSet(StringComparer.Ordinal);
        var byFileOrId = reading.PlaysByServer.Values.SelectMany(plays => plays).Any(play =>
            play.Paths.Any(path => files.Contains(RetentionServerIndex.NormalizePath(path))) ||
            (play.IsMovie && unit.Level == RetentionLevels.Movie && MoviePlayTouches(unit, play)) ||
            (play.IsEpisode && unit.Level != RetentionLevels.Movie && EpisodePlayTouches(unit, play)));

        return byItem || byFileOrId ? "Not removed: it is being played right now. Deluno tries again at the next check." : null;
    }

    /// <summary>
    /// Whether a film being played on ANY server may be this film. A shared provider id says yes; ids both sides
    /// carry that all differ say no; otherwise (no ids to compare, and its file is somewhere else) it cannot be
    /// told apart, so it counts as "may be" and the title waits (#1071).
    /// </summary>
    public static bool MoviePlayTouches(RetentionUnit unit, MediaServerPlay play)
    {
        static bool Both(string? a, string? b) => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b);
        static bool Same(string? a, string? b) => Both(a, b) && string.Equals(a!.Trim(), b!.Trim(), StringComparison.OrdinalIgnoreCase);

        if (Same(play.TmdbId, unit.TmdbId) || Same(play.ImdbId, unit.ImdbId) || Same(play.TvdbId, unit.TvdbId))
        {
            return true;
        }

        var comparable = Both(play.TmdbId, unit.TmdbId) || Both(play.ImdbId, unit.ImdbId) || Both(play.TvdbId, unit.TvdbId);
        return !comparable;
    }

    /// <summary>
    /// Whether an episode being played on ANY server may be inside this show, season or episode.
    /// The show is named by provider ids (then by name); the season and episode by their numbers. Anything
    /// that cannot be told apart counts as "may be", never as "not": unknown defers (#1071).
    /// </summary>
    public static bool EpisodePlayTouches(RetentionUnit unit, MediaServerPlay play)
    {
        static bool Same(string? a, string? b) => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        static string Letters(string? name) => new string((name ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        static bool Both(string? a, string? b) => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b);

        var sameShow = Same(play.ShowTmdbId, unit.TmdbId) || Same(play.ShowTvdbId, unit.TvdbId) || Same(play.ShowImdbId, unit.ImdbId);
        if (!sameShow)
        {
            var comparable = Both(play.ShowTmdbId, unit.TmdbId) || Both(play.ShowTvdbId, unit.TvdbId) || Both(play.ShowImdbId, unit.ImdbId);
            if (comparable)
            {
                // Both sides named the show by an id they share and none agree: a different show.
                return false;
            }

            if (Same(Letters(play.ShowName), Letters(unit.ShowTitle)))
            {
                sameShow = true;
            }
            else
            {
                // No id in common and a different (or missing) name: cannot tell, so wait.
                return true;
            }
        }

        return unit.Level switch
        {
            RetentionLevels.Show => true,
            RetentionLevels.Season => play.SeasonNumber is null || unit.SeasonNumber is null || play.SeasonNumber == unit.SeasonNumber,
            _ => play.SeasonNumber is null || play.EpisodeNumber is null || unit.SeasonNumber is null || unit.EpisodeNumber is null ||
                 (play.SeasonNumber == unit.SeasonNumber && play.EpisodeNumber == unit.EpisodeNumber)
        };
    }

    private async Task TellDeferredAsync(RetentionRule rule, IReadOnlyList<RetentionMark> deferred, PlaybackReading? reading, CancellationToken cancellationToken)
    {
        if (deferred.Count == 0)
        {
            return;
        }

        var why = reading?.CouldNotSee is null
            ? "it is being played right now"
            : $"Deluno could not see what is being played ({reading.CouldNotSee})";
        await activity.RecordActivityAsync(
            "retention.deferred",
            $"Retention rule “{rule.Name}” did not remove {List(deferred.Select(mark => mark.Title))}: {why}. It tries again at the next check.",
            JsonSerializer.Serialize(new { RuleId = rule.Id, EntityIds = deferred.Select(mark => mark.EntityId) }),
            null,
            "retention-rule",
            rule.Id,
            cancellationToken);
        if (reading?.CouldNotSee is not null)
        {
            await NotifyAsync(WaitingUnreachableEvent, $"{Titles(deferred.Count)} could not be removed yet", $"Retention rule “{rule.Name}”: {why}. {List(deferred.Select(mark => mark.Title))}.", rule, cancellationToken);
        }
    }

    // ----------------------------------------------------------- the day's action (#1011)

    /// <summary>What the rule does with a title on its day. Only <c>delete</c> removes anything.</summary>
    private async Task<RetentionRunItem> ActAsync(RetentionRule rule, RetentionMark mark, RetentionCandidate candidate, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (rule.ExpiryAction == RetentionExpiryActions.Delete)
        {
            return await RemoveAsync(rule, mark, candidate, now, cancellationToken);
        }

        try
        {
            string message;
            switch (rule.ExpiryAction)
            {
                case RetentionExpiryActions.Unmonitor:
                    message = await UnmonitorAsync(rule, candidate, cancellationToken);
                    break;
                case RetentionExpiryActions.Tag:
                    message = await TagAsync(rule, candidate, cancellationToken);
                    break;
                default:
                    message = "Its day came. The rule only records it, so nothing changed.";
                    break;
            }

            var (entityType, entityKey) = ActivityEntity(mark);
            await activity.RecordActivityAsync(
                "retention.action",
                $"{mark.Title}: {message} (retention rule “{rule.Name}”, {string.Join("; ", candidate.Reasons)}).",
                JsonSerializer.Serialize(new { RuleId = rule.Id, mark.EntityId, Action = rule.ExpiryAction }),
                null,
                entityType,
                entityKey,
                cancellationToken);
            await repository.UpsertMarkAsync(mark with { Reasons = candidate.Reasons, State = RetentionMarkStates.Done, StateReason = message, UpdatedUtc = now }, cancellationToken);
            return Item(mark with { Reasons = candidate.Reasons }, RetentionItemOutcomes.Done, message);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Retention rule {Rule} could not act on {Title}.", rule.Name, mark.Title);
            return Item(mark, RetentionItemOutcomes.Failed, exception.Message);
        }
    }

    private async Task<string> UnmonitorAsync(RetentionRule rule, RetentionCandidate candidate, CancellationToken cancellationToken)
    {
        if (rule.Level == RetentionLevels.Movie)
        {
            await movies.UpdateMonitoredAsync([candidate.EntityId], false, cancellationToken);
        }
        else if (rule.Level == RetentionLevels.Show)
        {
            await series.UpdateMonitoredAsync([candidate.EntityId], false, cancellationToken);
        }
        else
        {
            var episodeIds = candidate.Unit.Part?.EpisodeIds ?? [];
            if (episodeIds.Count == 0)
            {
                throw new InvalidOperationException("Deluno found no episodes to stop monitoring.");
            }

            await series.UpdateEpisodeMonitoredAsync(episodeIds.ToArray(), false, cancellationToken);
        }

        return "Stopped monitoring it; its files stay.";
    }

    /// <summary>
    /// Deluno is the *arr, so "tag" gives the title a Deluno tag. Tags live on movies and shows, so a
    /// season or episode gives its show the tag.
    /// </summary>
    private async Task<string> TagAsync(RetentionRule rule, RetentionCandidate candidate, CancellationToken cancellationToken)
    {
        if (titleTags is null)
        {
            throw new InvalidOperationException("Tags are not available in this build.");
        }

        var tag = rule.ExpiryTag?.Trim();
        if (string.IsNullOrWhiteSpace(tag))
        {
            throw new InvalidOperationException("The rule has no tag to give.");
        }

        var (kind, id) = rule.Level == RetentionLevels.Movie
            ? (MediaKind.Movie, candidate.EntityId)
            : (MediaKind.Series, candidate.Unit.ShowId ?? candidate.EntityId);
        var given = await titleTags.GiveAsync(kind, id, [tag], cancellationToken);
        return given.Count > 0 ? $"Tagged “{tag}”." : $"It already had the tag “{tag}”.";
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
            // A show still airing is not removed as a whole: only its aired episodes that have files go, each one
            // unmonitored on its own, so the episodes still to come are fetched as usual (#1072).
            var airing = rule.Level == RetentionLevels.Show ? await AiredPartOfAiringShowAsync(candidate, now, cancellationToken) : null;
            var granular = airing is not null || RetentionLevels.IsPartOfShow(rule.Level);
            var serverNotes = granular
                ? await RemovePartAsync(rule, airing ?? candidate, reason, now, cancellationToken)
                : await RemoveTitleAsync(rule, candidate, reason, cancellationToken);
            if (serverNotes is null)
            {
                await repository.DeleteMarkAsync(rule.Id, mark.EntityId, cancellationToken);
                return Item(mark, RetentionItemOutcomes.Cleared, "Already gone from Deluno.");
            }

            await repository.DeleteMarksForEntityAsync(mark.EntityId, cancellationToken);
            if (!granular)
            {
                // A show that goes takes its seasons' and episodes' marks with it.
                foreach (var part in await repository.ListMarksForTitleAsync(mark.EntityId, cancellationToken))
                {
                    await repository.DeleteMarkAsync(part.RuleId, part.EntityId, cancellationToken);
                }
            }

            var message = granular
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

    /// <summary>
    /// For a show that still has an episode to come, the show as its aired episodes with files: the part of it a rule may
    /// remove. Null for a show with nothing left to come, which is removed as a whole.
    /// </summary>
    private async Task<RetentionCandidate?> AiredPartOfAiringShowAsync(RetentionCandidate candidate, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var inventory = await series.GetInventoryDetailAsync(candidate.EntityId, cancellationToken);
        if (inventory is null || !inventory.Episodes.Any(episode => RetentionEpisodeMath.StillToCome(episode, now)))
        {
            return null;
        }

        var aired = inventory.Episodes
            .Where(episode => episode.HasFile && !RetentionEpisodeMath.EpisodeUpcoming(episode, now))
            .Select(episode => episode.EpisodeId)
            .ToArray();
        var part = new RetentionPartFacts(false, true, aired.Length, null, null, null, null, null, null, aired);
        return candidate with { Unit = candidate.Unit with { ShowId = candidate.Unit.EntityId, Part = part } };
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

        // Seerr would otherwise go on showing it available or requested (#522).
        // A failure here never undoes the removal; it is owed and tried again.
        var seerrNote = await seerr.ClearAsync(candidate.EntityId, rule.MediaType, candidate.Title, target.Value.TmdbId, candidate.Unit.TvdbId, cancellationToken);
        var notes = rule.RemoveFromServers
            ? await TellServersAsync(candidate.ServerItems.Select(match => (match.Server, match.Item)), candidate.Title, cancellationToken)
            : [];
        return seerrNote is null ? notes : [.. notes, $"Seerr: {seerrNote}"];
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
        await Deluno.Jobs.Data.MediaServerTell.QueueAsync(
            mediaServerJobScheduler,
            "tv",
            files.Select(file => new MediaServerPathChange(file.FilePath, MediaServerChangeKind.Deleted)).ToArray(),
            "retention.removed",
            "series",
            unit.ShowId,
            logger,
            cancellationToken);
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

        var showTags = mediaTagStore is null
            ? null
            : (await mediaTagStore.ListAsync(MediaKind.Series, unit.ShowId, cancellationToken)).Select(assignment => assignment.Name).ToArray();
        await NotificationOutboundDispatch.DispatchIfEnabledAsync(
            notifications,
            notificationPreferences,
            "file.deleted",
            $"Files removed: {unit.Title}",
            $"{unit.Title}: {sentence}.",
            JsonSerializer.Serialize(new { RuleId = rule.Id, unit.ShowId, EpisodeIds = episodeIds, FileCount = files.Count }),
            prefs => prefs.FileDeletedEnabled,
            "retention-episode-removal",
            logger,
            cancellationToken,
            showTags);

        if (!rule.RemoveFromServers)
        {
            return [];
        }

        // A season is told episode by episode: those are the items that hold files.
        var paths = files.Select(file => RetentionServerIndex.NormalizePath(file.FilePath)).ToHashSet(StringComparer.Ordinal);
        var told = candidate.ServerItems.SelectMany(match => match.Episodes.Count > 0
            ? match.Episodes.Where(episode => episode.Paths.Any(path => paths.Contains(RetentionServerIndex.NormalizePath(path)))).Select(episode => (match.Server, Item: episode))
            : unit.Level == RetentionLevels.Show ? [] : [(match.Server, match.Item)]);
        return await TellServersAsync(told, unit.Title, cancellationToken, episodesOnly: unit.Level == RetentionLevels.Show);
    }

    private async Task<IReadOnlyList<string>> TellServersAsync(
        IEnumerable<(Deluno.Connections.Contracts.MediaServerConnection Server, MediaServerItem Item)> items,
        string title,
        CancellationToken cancellationToken,
        bool episodesOnly = false)
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
                ? (episodesOnly ? $"{server.Name} was told {group.Count()} {(group.Count() == 1 ? "episode" : "episodes")} of {title} {(group.Count() == 1 ? "is" : "are")} gone." : $"{server.Name} was told {title} is gone.")
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
            await NotifyAsync(WaitingConfirmationEvent, $"{Titles(ready.Count)} ready to remove",
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

    /// <summary>What the title was matched to on each server, for the Leaving soon collections (#521).</summary>
    private static IReadOnlyList<RetentionServerItem> ServerItems(RetentionCandidate candidate)
        => candidate.ServerItems
            .Select(match => new RetentionServerItem(match.Server.Id, match.Item.ServerItemId, match.Item.Level, match.Item.SectionId))
            .ToArray();

    private static string Summary(int marked, int cleared, int removed, long freed, int ready, int held, int failed, int leaving, string? done = null, int deferred = 0, int upcoming = 0)
    {
        var said = SummaryCore(marked, cleared, removed, freed, ready, held, failed, leaving, done, deferred);
        return upcoming > 0
            ? $"{said} {UpcomingSentence(upcoming)}"
            : said;
    }

    /// <summary>Plain words for what the rule skipped because it has not aired yet (#1072).</summary>
    private static string UpcomingSentence(int count)
        => count == 1
            ? "1 episode, season or show that has not aired yet was left alone: a rule never touches anything upcoming."
            : $"{count} episodes, seasons or shows that have not aired yet were left alone: a rule never touches anything upcoming.";

    private static string SummaryCore(int marked, int cleared, int removed, long freed, int ready, int held, int failed, int leaving, string? done, int deferred)
    {
        var parts = new List<string>();
        if (marked > 0) parts.Add($"{Titles(marked)} marked leaving soon");
        if (cleared > 0) parts.Add($"{Titles(cleared)} no longer leaving");
        if (removed > 0) parts.Add($"{Titles(removed)} removed, freeing {FormatBytes(freed)}");
        if (done is not null) parts.Add(done);
        if (ready > 0) parts.Add($"{Titles(ready)} ready to remove");
        if (deferred > 0) parts.Add($"{Titles(deferred)} left alone while being played, or while what is being played could not be seen");
        if (held - deferred > 0) parts.Add($"{Titles(held - deferred)} held");
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
