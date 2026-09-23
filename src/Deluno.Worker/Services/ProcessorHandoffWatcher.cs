using System.Text.Json;
using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Series.Contracts;
using Deluno.Series.Data;

namespace Deluno.Worker.Services;

/// <summary>
/// Keeps track of files that are with a processor by asking it, not by timing
/// it (#511).
///
/// <para><b>What replaced the timeout.</b> Deluno used to give up six hours after
/// handing a file over, and an output that arrived later was never imported.
/// The owner's objection was that no fixed limit fits something this dynamic:
/// a processor with an overnight schedule, a queue of 4K files or a patient
/// retry policy legitimately holds a file for a day. So a processor that can
/// answer is asked, every pass, what it is doing with each file:</para>
///
/// <list type="bullet">
/// <item>queued, scheduled, working — keep waiting, and show the answer;</item>
/// <item>never heard of it — stop, and offer to import the original;</item>
/// <item>not answering — keep waiting and say so; the clock does not run while
/// it cannot be asked;</item>
/// <item>no change at all for the library's limit (24 hours to 7 days, 48 by
/// default) while it answers and has no run scheduled ahead — the library's
/// failure rule decides, and the processor is asked to drop the file first.</item>
/// </list>
///
/// <para>A processor that cannot be asked is judged the same way, with the
/// hand-off's own last change standing in for the processor's. And whatever the
/// ending, a processed file that turns up afterwards is still imported.</para>
/// </summary>
public sealed class ProcessorHandoffWatcher(
    IProcessorRepository processorRepository,
    IProcessorConnectionService processorConnections,
    IActivityFeedRepository activityFeed,
    IMovieCatalogRepository movieCatalogRepository,
    ISeriesCatalogRepository seriesCatalogRepository)
{
    /// <summary>
    /// A processor that has just accepted a hand-off may not have written it
    /// down yet. A "never heard of it" inside this window is not believed.
    /// </summary>
    public static readonly TimeSpan NotFoundGrace = TimeSpan.FromMinutes(15);

    /// <summary>How long a processor can be unreachable before Deluno says so.</summary>
    public static readonly TimeSpan UnreachableNoticeAfter = TimeSpan.FromHours(1);

    /// <summary>
    /// How many times the library's limit a processor may hold a file before
    /// Deluno stops waiting whatever it says (#572). Generous, because the
    /// limit is what the person chose and this is only the backstop for a
    /// processor whose answers keep the ordinary rules from ever applying.
    /// </summary>
    private const int HeldTooLongMultiplier = 3;

    /// <param name="libraries">The libraries that can be worked on right now.</param>
    /// <param name="allLibraries">
    /// Every library that exists, reachable or not. A hand-off is orphaned only
    /// when its library is gone from here or no longer processes before import;
    /// a library whose folder is merely unreachable keeps its hand-offs.
    /// </param>
    public async Task WatchAsync(
        IReadOnlyList<LibraryItem> libraries,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        IReadOnlyList<LibraryItem>? allLibraries = null)
    {
        // Exactly the hand-offs a processor still has, however much history sits
        // in front of them (#571).
        var handoffs = await processorRepository.ListProcessorHandoffsWithStatusAsync(
            ProcessorHandoffStatuses.WithProcessor,
            cancellationToken);
        var connections = new Dictionary<string, ProcessorConnectionItem?>(StringComparer.OrdinalIgnoreCase);
        var support = new Dictionary<string, ProcessorStatusSupport>(StringComparer.OrdinalIgnoreCase);
        var connectionsNoticed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var handoff in handoffs.Where(item => ProcessorHandoffStatuses.IsWithProcessor(item.Status)))
        {
            // Nothing is waiting for this hand-off any more. It used to be
            // skipped here, which left it open for ever: no limit applied, no
            // screen could close it, and Transfers went on counting it as work
            // in progress. Seen on the rig after a proof library was removed.
            var owner = (allLibraries ?? libraries).FirstOrDefault(item =>
                string.Equals(item.Id, handoff.LibraryId, StringComparison.OrdinalIgnoreCase));
            if (owner is null || !ImportWorkflows.HandsToProcessor(owner.ImportWorkflow))
            {
                await EndAsync(
                    handoff,
                    ProcessorHandoffStatuses.Cancelled,
                    owner is null
                        ? $"The library {handoff.ReleaseName} was downloaded for has been removed, so nothing is waiting for a processed copy."
                        : $"{owner.Name} no longer processes downloads before importing them, so nothing is waiting for a processed copy.",
                    cancellationToken);
                continue;
            }

            // A hand-off still waiting to be sent is the planner's to send.
            if (!ProcessorHandoffStatuses.IsOpen(handoff.Status))
            {
                continue;
            }

            var library = libraries.FirstOrDefault(item =>
                string.Equals(item.Id, handoff.LibraryId, StringComparison.OrdinalIgnoreCase));
            if (library is null)
            {
                // Its folder cannot be reached right now. That is not an ending.
                continue;
            }

            var connectionName = library.ProcessorName ?? handoff.ProcessorName ?? string.Empty;
            if (!connections.TryGetValue(connectionName, out var connection))
            {
                connection = await processorRepository.FindProcessorConnectionByNameAsync(connectionName, cancellationToken);
                connections[connectionName] = connection;
            }

            var limit = TimeSpan.FromMinutes(ProcessorStallLimits.Normalize(library.ProcessorTimeoutMinutes));
            var processorName = connection?.Name ?? library.ProcessorName ?? "The processor";

            if (connection is not { IsEnabled: true })
            {
                await JudgeWithoutAskingAsync(handoff, library, processorName, limit, now, cancellationToken);
                continue;
            }

            if (!support.TryGetValue(connection.Id, out var canAsk))
            {
                canAsk = await processorConnections.GetStatusSupportAsync(connection, cancellationToken);
                support[connection.Id] = canAsk;
            }

            if (canAsk.Answer is ProcessorAnswer.Unreachable or ProcessorAnswer.Forbidden)
            {
                await ObserveSilenceAsync(handoff, connection, canAsk.Answer, canAsk.Message, now, connectionsNoticed, cancellationToken);
                continue;
            }

            if (!canAsk.CanReportStatus)
            {
                await JudgeWithoutAskingAsync(handoff, library, processorName, limit, now, cancellationToken);
                continue;
            }

            var answer = await processorConnections.GetHandoffStatusAsync(connection, handoff.Id, cancellationToken);
            switch (answer.Answer)
            {
                case ProcessorAnswer.Answered:
                    await ObserveAnswerAsync(handoff, library, connection, canAsk, answer, limit, now, cancellationToken);
                    break;

                case ProcessorAnswer.NotFound when now - handoff.UpdatedUtc >= NotFoundGrace:
                    await RecordLostAsync(handoff, library, processorName, cancellationToken);
                    break;

                case ProcessorAnswer.NotFound:
                    break;

                default:
                    await ObserveSilenceAsync(handoff, connection, answer.Answer, answer.Message, now, connectionsNoticed, cancellationToken);
                    break;
            }
        }
    }

    /// <summary>
    /// A processor that cannot be asked. The hand-off's last recorded change -
    /// submission, or an accepted or started event - is the only sign of life,
    /// so the same limit is measured from that.
    /// </summary>
    private async Task JudgeWithoutAskingAsync(
        ProcessorHandoffItem handoff,
        LibraryItem library,
        string processorName,
        TimeSpan limit,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (now - handoff.UpdatedUtc <= limit)
        {
            return;
        }

        await GiveUpAsync(
            handoff,
            library,
            ProcessorHandoffStatuses.TimedOut,
            $"{handoff.ReleaseName}: no word from {processorName} for {Describe(now - handoff.UpdatedUtc)}, and it cannot be asked what it is doing.",
            cancellationToken);
    }

    private async Task ObserveAnswerAsync(
        ProcessorHandoffItem handoff,
        LibraryItem library,
        ProcessorConnectionItem connection,
        ProcessorStatusSupport canAsk,
        ProcessorHandoffStatusResult answer,
        TimeSpan limit,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var previous = handoff.Processor;
        var answeringSince = previous is null || previous.UnreachableSinceUtc is not null || previous.AnsweringSinceUtc is null
            ? now
            : previous.AnsweringSinceUtc;
        var observation = new ProcessorHandoffObservation(
            answer.State,
            answer.Message,
            answer.QueuePosition,
            answer.ScheduledForUtc,
            answer.LastChangedUtc,
            now,
            null,
            answeringSince);
        await processorRepository.RecordProcessorObservationAsync(handoff.Id, observation, cancellationToken);

        switch (answer.State)
        {
            case ProcessorHandoffStates.Completed or ProcessorHandoffStates.PassedThrough:
                // Its report or the output folder brings the file in, and either
                // one closes the hand-off. If neither has by the library's limit,
                // nothing will: the output went somewhere Deluno does not look,
                // or an earlier download of the same release already imported
                // it. Left open, the hand-off also claimed every later download
                // into the same folder, and those never reached the processor
                // (#561). The processor's own "finished at" is the clock:
                // Deluno's answering streak restarts whenever the processor
                // does, which would put the limit off every time.
                var finishedFor = now - (answer.LastChangedUtc ?? answeringSince ?? handoff.UpdatedUtc);
                if (finishedFor > limit)
                {
                    await GiveUpAsync(
                        handoff,
                        library,
                        ProcessorHandoffStatuses.TimedOut,
                        $"{connection.Name} finished {handoff.ReleaseName} {Describe(finishedFor)} ago, but no processed file arrived for Deluno to import.",
                        cancellationToken);
                }

                return;

            case ProcessorHandoffStates.Queued or ProcessorHandoffStates.Scheduled or ProcessorHandoffStates.Working:
                break;

            case ProcessorHandoffStates.Cancelled:
                await EndAsync(handoff, ProcessorHandoffStatuses.Cancelled, $"{connection.Name} stopped work on {handoff.ReleaseName}.", cancellationToken);
                return;

            default:
                // failed, rejected, or a word Deluno does not know: the processor
                // is not going to deliver. Its own report normally closes the
                // hand-off first; this catches a report that never arrived.
                var reason = answer.Message is { Length: > 0 } said
                    ? $"{connection.Name} reports {answer.State} for {handoff.ReleaseName}: {said}"
                    : $"{connection.Name} reports {answer.State} for {handoff.ReleaseName}.";
                await EndAsync(handoff, ProcessorHandoffStatuses.Failed, reason, cancellationToken);
                await RaiseRecoveryCaseAsync(
                    handoff,
                    library,
                    "processor-failed",
                    reason,
                    "Open Queue to import the original download, or retry the hand-off once the processor problem is fixed.",
                    cancellationToken);
                return;
        }

        // However politely a processor is behaving, a file it has held for days
        // is a problem for a person (#572).
        //
        // Every rule below measures from the processor's own last change, or
        // from it answering again after being unreachable - so a processor that
        // goes quiet for one pass and comes back resets the clock to zero, and
        // one that keeps rescheduling, keeps saying "working", or attaches a
        // note to "queued" is exempt from the rules entirely. Each of those
        // holds a file for the whole of a long run with nothing on any screen
        // saying so, because a hand-off still with a processor is deliberately
        // not counted. This is the one measurement none of that can move.
        var heldFor = now - handoff.CreatedUtc;
        if (heldFor > TimeSpan.FromTicks(limit.Ticks * HeldTooLongMultiplier))
        {
            await GiveUpAsync(
                handoff,
                library,
                ProcessorHandoffStatuses.TimedOut,
                $"{connection.Name} has had {handoff.ReleaseName} for {Describe(heldFor)} and still says {Say(answer)}. Deluno stopped waiting.",
                cancellationToken);
            return;
        }

        if (answer.State == ProcessorHandoffStates.Scheduled && answer.ScheduledForUtc > now)
        {
            return;
        }

        // Queued with an explanation is the processor waiting on Deluno - it
        // holds a file while Deluno is mid-import - which is never a stall.
        if (answer.State == ProcessorHandoffStates.Queued && !string.IsNullOrWhiteSpace(answer.Message))
        {
            return;
        }

        var clockStart = Latest(answer.LastChangedUtc ?? handoff.UpdatedUtc, answeringSince ?? now);
        var quiet = now - clockStart;
        if (quiet <= limit)
        {
            return;
        }

        if (answer.State == ProcessorHandoffStates.Working)
        {
            // It says it is working. It will not be interrupted mid-run, so the
            // most Deluno can do is say, once, that nothing has moved.
            if (handoff.FailureMessage is null)
            {
                var notice = $"{connection.Name} has been working on {handoff.ReleaseName} for {Describe(quiet)} with no change reported.";
                await processorRepository.UpdateProcessorHandoffAsync(handoff.Id, handoff.Status, null, null, notice, cancellationToken);
                await activityFeed.RecordActivityAsync(
                    "processing.no-progress",
                    notice,
                    Details(handoff, library, answer.State),
                    null,
                    "processor-handoff",
                    handoff.Id,
                    cancellationToken);
            }

            return;
        }

        if (canAsk.CanCancel)
        {
            var cancel = await processorConnections.CancelHandoffAsync(connection, handoff.Id, cancellationToken);
            if (cancel.Answer is ProcessorAnswer.Busy or ProcessorAnswer.Unreachable or ProcessorAnswer.Forbidden)
            {
                // Started in the meantime, or could not be told: waiting on is
                // the only answer that cannot import a file twice.
                return;
            }
        }

        await GiveUpAsync(
            handoff,
            library,
            ProcessorHandoffStatuses.Stalled,
            $"{handoff.ReleaseName}: {connection.Name} reported no change for {Describe(quiet)} ({Say(answer)}).",
            cancellationToken);
    }

    /// <summary>
    /// A processor that could not be asked. The file is not given up on: the
    /// record says when the silence started, and the no-progress clock restarts
    /// from the moment it answers again.
    /// </summary>
    private async Task ObserveSilenceAsync(
        ProcessorHandoffItem handoff,
        ProcessorConnectionItem connection,
        ProcessorAnswer answer,
        string? message,
        DateTimeOffset now,
        ISet<string> connectionsNoticed,
        CancellationToken cancellationToken)
    {
        var previous = handoff.Processor;
        var silentSince = previous?.UnreachableSinceUtc ?? now;
        await processorRepository.RecordProcessorObservationAsync(
            handoff.Id,
            new ProcessorHandoffObservation(
                previous?.State,
                message,
                previous?.QueuePosition,
                previous?.ScheduledForUtc,
                previous?.LastChangedUtc,
                now,
                silentSince,
                null),
            cancellationToken);

        var justCrossed = now - silentSince >= UnreachableNoticeAfter &&
                          (previous?.CheckedUtc is null || previous.CheckedUtc - silentSince < UnreachableNoticeAfter);
        var forbidden = answer == ProcessorAnswer.Forbidden;
        if ((forbidden || justCrossed) && connectionsNoticed.Add(connection.Id))
        {
            var text = forbidden
                ? message ?? $"{connection.Name} will not describe its hand-offs to Deluno."
                : $"{connection.Name} has not answered for {Describe(now - silentSince)}. Deluno is still waiting for the files it handed over and will not give up on them while it cannot ask.";
            await processorRepository.RecordProcessorConnectionHealthAsync(
                connection.Id,
                forbidden ? "degraded" : "unreachable",
                text,
                cancellationToken);
            if (!forbidden || previous?.Message != message)
            {
                await activityFeed.RecordActivityAsync(
                    forbidden ? "processing.status-forbidden" : "processing.processor-unreachable",
                    text,
                    JsonSerializer.Serialize(new { ConnectionId = connection.Id, connection.Name, HandoffId = handoff.Id }),
                    null,
                    "processor-connection",
                    connection.Id,
                    cancellationToken);
            }
        }
    }

    private async Task RecordLostAsync(
        ProcessorHandoffItem handoff,
        LibraryItem library,
        string processorName,
        CancellationToken cancellationToken)
    {
        var summary = $"{processorName} has no record of {handoff.ReleaseName}, so nothing is coming back for it.";
        await EndAsync(handoff, ProcessorHandoffStatuses.Lost, summary, cancellationToken);
        await RaiseRecoveryCaseAsync(
            handoff,
            library,
            "processor-lost",
            summary,
            "Open Queue and choose Import original, or retry the hand-off.",
            cancellationToken);
    }

    /// <summary>
    /// The library's failure rule, applied. "Import original" hands the untouched
    /// download to the ordinary import path; the other two leave it for a person
    /// and say what to do.
    /// </summary>
    private async Task GiveUpAsync(
        ProcessorHandoffItem handoff,
        LibraryItem library,
        string endStatus,
        string summary,
        CancellationToken cancellationToken)
    {
        if (string.Equals(library.ProcessorFailureMode, "import-original", StringComparison.OrdinalIgnoreCase))
        {
            await EndAsync(
                handoff,
                ProcessorHandoffStatuses.Released,
                $"{summary} {library.Name} imports the original when processing stalls, so Deluno is importing it unprocessed.",
                cancellationToken);
            return;
        }

        await EndAsync(handoff, endStatus, summary, cancellationToken);
        await RaiseRecoveryCaseAsync(
            handoff,
            library,
            endStatus == ProcessorHandoffStatuses.TimedOut ? "processor-timeout" : "processor-stalled",
            summary,
            string.Equals(library.ProcessorFailureMode, "manual-review", StringComparison.OrdinalIgnoreCase)
                ? "Open Queue to import the original, retry the hand-off, or dismiss it."
                : "Check the processor, then retry the hand-off. Queue can import the original if it is acceptable.",
            cancellationToken);
    }

    private async Task EndAsync(ProcessorHandoffItem handoff, string status, string message, CancellationToken cancellationToken)
    {
        await processorRepository.UpdateProcessorHandoffAsync(handoff.Id, status, null, null, message, cancellationToken);
        await activityFeed.RecordActivityAsync(
            $"processing.{status}",
            message,
            JsonSerializer.Serialize(new { HandoffId = handoff.Id, handoff.LibraryId, handoff.ReleaseName, handoff.SourcePath, Status = status }),
            null,
            "processor-handoff",
            handoff.Id,
            cancellationToken);
    }

    private async Task RaiseRecoveryCaseAsync(
        ProcessorHandoffItem handoff,
        LibraryItem library,
        string kind,
        string summary,
        string recommended,
        CancellationToken cancellationToken)
    {
        var details = Details(handoff, library, handoff.Processor?.State);
        if (DelunoValueNormalizers.NormalizeMediaType(library.MediaType) == "tv")
        {
            await seriesCatalogRepository.AddImportRecoveryCaseAsync(
                new CreateSeriesImportRecoveryCaseRequest(handoff.ReleaseName, kind, summary, recommended, details),
                cancellationToken);
        }
        else
        {
            await movieCatalogRepository.AddImportRecoveryCaseAsync(
                new CreateMovieImportRecoveryCaseRequest(handoff.ReleaseName, kind, summary, recommended, details),
                cancellationToken);
        }
    }

    private static string Details(ProcessorHandoffItem handoff, LibraryItem library, string? processorState)
        => JsonSerializer.Serialize(new
        {
            libraryId = library.Id,
            handoffId = handoff.Id,
            releaseName = handoff.ReleaseName,
            sourcePath = handoff.SourcePath,
            processorState
        });

    private static DateTimeOffset Latest(DateTimeOffset left, DateTimeOffset right) => left > right ? left : right;

    private static string Say(ProcessorHandoffStatusResult answer)
        => answer.State switch
        {
            ProcessorHandoffStates.Queued when answer.QueuePosition is { } position => $"still queued, position {position}",
            ProcessorHandoffStates.Scheduled => "waiting for a scheduled run that has passed",
            _ => $"still {answer.State}"
        };

    internal static string Describe(TimeSpan span)
        => span.TotalHours >= 48
            ? $"{Math.Floor(span.TotalDays):0} days"
            : $"{Math.Floor(span.TotalHours):0} hours";
}
