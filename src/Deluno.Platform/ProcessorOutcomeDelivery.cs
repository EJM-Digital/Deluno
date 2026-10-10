using System.Text.Json;
using Deluno.Jobs.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;

namespace Deluno.Platform;

/// <summary>
/// Telling a processor what became of a file it handed back, until it has
/// heard (#578, #580).
///
/// <para><b>Why this is more than one call.</b> The outcome exists so a
/// processor can let go of its copy. Sent once, one blip at the wrong moment
/// lost it for good: a processor restarting in the seconds after an import, a
/// dropped request, or an answer Deluno read as final that was not — Weir 3.2.0
/// refused one for a hand-off it had itself just completed. Each is a copy the
/// processor keeps until its own retention reclaims it, which is the leak #578
/// was opened to close. So what is owed is written down first, and said again
/// until it is acknowledged or saying it again is provably pointless.</para>
///
/// <para>Three callers — the import that just finished, the scheduled pass that
/// says it again, and a person asking from the hand-off's drawer — and one body,
/// because what counts as heard is the part a second copy would get subtly
/// different.</para>
/// </summary>
public static class ProcessorOutcomeDelivery
{
    /// <summary>
    /// How long the scheduled pass leaves a report that is being sent right now.
    /// Longer than one attempt takes, so the pass does not send it a second time
    /// alongside the first; short enough that a crash mid-attempt is caught.
    /// </summary>
    public static readonly TimeSpan InFlightGrace = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Writes down what the processor is owed, then says it.
    ///
    /// <para>Nothing is recorded when the hand-off has no processor connection
    /// by that name: there is nobody to owe.</para>
    ///
    /// <para>Never throws for the processor's sake: a processor disagreeing
    /// about its own bookkeeping is not a reason to undo work that has already
    /// happened.</para>
    /// </summary>
    public static async Task<ProcessorOutcomeRecord?> RecordAndSendAsync(
        IProcessorRepository repository,
        IProcessorConnectionService processorConnections,
        IActivityFeedRepository activityFeed,
        ProcessorHandoffItem handoff,
        ProcessorHandoffOutcome outcome,
        string? importedPath,
        string? reason,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var connection = await repository.FindProcessorConnectionAsync(handoff.ProcessorConnectionId, handoff.ProcessorName, cancellationToken);
        if (connection is null) return null;

        var record = new ProcessorOutcomeRecord(
            handoff.Id,
            connection.Name,
            handoff.LibraryId,
            handoff.ReleaseName,
            ProcessorOutcomeRecord.Word(outcome),
            outcome == ProcessorHandoffOutcome.Imported ? importedPath : null,
            // Reason is not only "why not imported" any more: an imported
            // season pack carries which of its files were skipped and why
            // here too (#652), so the processor - and the drawer - hear the
            // whole story, not just "it has it".
            reason,
            nowUtc,
            ProcessorOutcomeDeliveries.Pending,
            0,
            null,
            nowUtc + InFlightGrace,
            false,
            null,
            nowUtc,
            connection.Id);
        await repository.SaveProcessorOutcomeAsync(record, cancellationToken);
        return await SendAsync(repository, processorConnections, activityFeed, record, nowUtc, cancellationToken);
    }

    /// <summary>
    /// Says it again from the start, because a person asked: the attempt count
    /// and the week before Deluno gives up both begin again. The report itself —
    /// what happened and when — does not change.
    /// </summary>
    public static Task<ProcessorOutcomeRecord> SendAgainAsync(
        IProcessorRepository repository,
        IProcessorConnectionService processorConnections,
        IActivityFeedRepository activityFeed,
        ProcessorOutcomeRecord record,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
        => SendAsync(
            repository,
            processorConnections,
            activityFeed,
            record with
            {
                Delivery = ProcessorOutcomeDeliveries.Pending,
                Attempts = 0,
                NextAttemptUtc = nowUtc + InFlightGrace,
                RecordedUtc = nowUtc
            },
            nowUtc,
            cancellationToken);

    /// <summary>One attempt, and what it leaves the record as.</summary>
    public static async Task<ProcessorOutcomeRecord> SendAsync(
        IProcessorRepository repository,
        IProcessorConnectionService processorConnections,
        IActivityFeedRepository activityFeed,
        ProcessorOutcomeRecord record,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        // "It will not import this" about a send that a newer send of the same release replaced, while that newer send
        // is being handled or is done, would contradict the report the newer send makes about the one file both
        // refer to (#1226). It is not told: the newer send tells the processor what became of the file.
        if (record.Outcome == ProcessorOutcomeRecord.NotImported
            && record.Delivery == ProcessorOutcomeDeliveries.Pending
            && await repository.FindSupersededProcessorHandoffAsync(record.LibraryId, record.HandoffId, cancellationToken) is { } older
            && await repository.GetProcessorHandoffAsync(older.SupersededById, cancellationToken) is { } successor
            && (ProcessorHandoffStatuses.IsWithProcessor(successor.Status) || successor.Status == ProcessorHandoffStatuses.Completed))
        {
            return await SettleAsync(
                repository,
                activityFeed,
                record with
                {
                    Delivery = ProcessorOutcomeDeliveries.Replaced,
                    NextAttemptUtc = null,
                    LastAttemptUtc = nowUtc,
                    ProcessorSaid = $"{record.ReleaseName} was sent again after this send, and the newer send is the one that is handled."
                },
                announce: true,
                cancellationToken);
        }

        var connection = await repository.FindProcessorConnectionAsync(record.ProcessorConnectionId, record.ProcessorName, cancellationToken);
        if (connection is null)
        {
            return await SettleAsync(
                repository,
                activityFeed,
                record with
                {
                    Delivery = ProcessorOutcomeDeliveries.GivenUp,
                    NextAttemptUtc = null,
                    ProcessorSaid = $"The connection to {record.ProcessorName} was removed, so there is nobody left to tell."
                },
                announce: true,
                cancellationToken);
        }

        if (!connection.IsEnabled)
        {
            // Switched off in Deluno is a person's choice, and not talking to it
            // is what that means. It is not an attempt, but the week still runs.
            return await PendingOrGivenUpAsync(
                repository,
                activityFeed,
                record,
                record.Attempts,
                $"{connection.Name} is switched off in Deluno, so it has not been told yet.",
                nowUtc,
                announce: false,
                cancellationToken);
        }

        var support = await processorConnections.GetStatusSupportAsync(connection, cancellationToken);
        if (support.Answer is not (ProcessorAnswer.Unreachable or ProcessorAnswer.Forbidden) && !support.CanBeToldTheOutcome)
        {
            // It answered, and does not ask to be told. That is how it works
            // with every media manager that says nothing, and it is not a
            // failure. Written down so the drawer can say so; not announced.
            return await SettleAsync(
                repository,
                activityFeed,
                record with
                {
                    Delivery = ProcessorOutcomeDeliveries.GivenUp,
                    NextAttemptUtc = null,
                    LastAttemptUtc = nowUtc,
                    ProcessorSaid = $"{connection.Name} does not ask to be told what became of its files, so it keeps its copy until its own retention reclaims it."
                },
                announce: false,
                cancellationToken);
        }

        var told = support.Answer is ProcessorAnswer.Unreachable or ProcessorAnswer.Forbidden
            ? new ProcessorOutcomeResult(support.Answer, false, support.Message ?? $"Deluno could not reach {connection.Name}.")
            : await processorConnections.ReportOutcomeAsync(
                connection,
                record.HandoffId,
                record.AsOutcome(),
                record.ImportedPath,
                record.Reason,
                record.OccurredUtc,
                cancellationToken);
        var attempts = record.Attempts + 1;
        var delivery = ProcessorOutcomeDeliveries.After(told.Answer);
        var attempted = record with { Attempts = attempts, LastAttemptUtc = nowUtc, Released = told.Released };

        if (delivery == ProcessorOutcomeDeliveries.Pending)
        {
            // Said once, on the first miss, and again only when it ends: a
            // processor down for a day would otherwise write a line an hour.
            return await PendingOrGivenUpAsync(
                repository,
                activityFeed,
                attempted,
                attempts,
                told.Message ?? $"{connection.Name} did not acknowledge it.",
                nowUtc,
                announce: attempts == 1,
                cancellationToken);
        }

        return await SettleAsync(
            repository,
            activityFeed,
            // What is kept is what the processor said that agrees with this outcome (#1242): its reply can carry the
            // stored note of another send of the same release, and the record is read back on the hand-off's drawer.
            attempted with { Delivery = delivery, NextAttemptUtc = null, ProcessorSaid = delivery is ProcessorOutcomeDeliveries.Delivered or ProcessorOutcomeDeliveries.Settled
                    ? OwnWords(told.Message, record.Outcome == ProcessorOutcomeRecord.Imported)
                    : told.Message },
            announce: true,
            cancellationToken);
    }

    private static Task<ProcessorOutcomeRecord> PendingOrGivenUpAsync(
        IProcessorRepository repository,
        IActivityFeedRepository activityFeed,
        ProcessorOutcomeRecord record,
        int attempts,
        string said,
        DateTimeOffset nowUtc,
        bool announce,
        CancellationToken cancellationToken)
    {
        if (nowUtc - record.RecordedUtc >= ProcessorOutcomeDeliveries.GiveUpAfter)
        {
            return SettleAsync(
                repository,
                activityFeed,
                record with
                {
                    Delivery = ProcessorOutcomeDeliveries.GivenUp,
                    NextAttemptUtc = null,
                    ProcessorSaid = $"Nobody acknowledged it in {ProcessorOutcomeDeliveries.GiveUpAfter.TotalDays:0} days, so Deluno stopped trying. Last time: {said}"
                },
                announce: true,
                cancellationToken);
        }

        return SettleAsync(
            repository,
            activityFeed,
            record with
            {
                Delivery = ProcessorOutcomeDeliveries.Pending,
                NextAttemptUtc = nowUtc + ProcessorOutcomeDeliveries.WaitAfter(Math.Max(attempts, 1)),
                ProcessorSaid = said
            },
            announce,
            cancellationToken);
    }

    private static async Task<ProcessorOutcomeRecord> SettleAsync(
        IProcessorRepository repository,
        IActivityFeedRepository activityFeed,
        ProcessorOutcomeRecord record,
        bool announce,
        CancellationToken cancellationToken)
    {
        await repository.SaveProcessorOutcomeAsync(record, cancellationToken);
        if (!announce) return record;

        var (kind, message) = Describe(record);
        await activityFeed.RecordActivityAsync(
            kind,
            message,
            JsonSerializer.Serialize(new
            {
                record.HandoffId,
                record.LibraryId,
                record.ReleaseName,
                record.Outcome,
                record.Delivery,
                record.Attempts,
                record.Released,
                record.ProcessorSaid
            }),
            null,
            "processor-handoff",
            record.HandoffId,
            cancellationToken);
        return record;
    }

    /// <summary>What a person reads in Activity, in one sentence per ending.</summary>
    public static (string Kind, string Message) Describe(ProcessorOutcomeRecord record)
    {
        // Nothing was refused and nothing failed in these two: no "could not tell" and no stop-trying wording (#1226, #1230).
        if (record.Delivery == ProcessorOutcomeDeliveries.NeverReceived)
        {
            return (
                "processing.outcome-never-received",
                $"{record.ProcessorName} never received this hand-off of {record.ReleaseName}, so there was nothing to tell it.");
        }

        if (record.Delivery == ProcessorOutcomeDeliveries.Replaced)
        {
            return (
                "processing.outcome-superseded",
                $"{record.ReleaseName} was sent to {record.ProcessorName} again after this send, so Deluno left the report to the newer send, which is the one that is handled.");
        }

        var imported = record.Outcome == ProcessorOutcomeRecord.Imported;
        var said = imported
            ? $"Deluno told {record.ProcessorName} it has {record.ReleaseName}"
                + (string.IsNullOrWhiteSpace(record.Reason) ? "" : $" ({record.Reason.TrimEnd().TrimEnd('.')})")
            : $"Deluno told {record.ProcessorName} it will not import {record.ReleaseName}: {record.Reason?.TrimEnd().TrimEnd('.')}";
        var tries = record.Attempts > 1 ? $" on attempt {record.Attempts}" : "";
        // What the processor says back can carry its stored note from another send of the same release (#1232).
        // One hand-off's line only repeats what agrees with its own outcome.
        var theirs = OwnWords(record.ProcessorSaid, imported);
        return record.Delivery switch
        {
            ProcessorOutcomeDeliveries.Delivered => (
                "processing.outcome-reported",
                $"{said}{tries}. {(record.Released ? "It released its copy." : theirs ?? "It kept its copy.")}".TrimEnd()),
            ProcessorOutcomeDeliveries.Settled => (
                "processing.outcome-kept",
                $"{record.ProcessorName} kept its earlier answer about {record.ReleaseName}, so Deluno stopped telling it. {theirs}".TrimEnd()),
            ProcessorOutcomeDeliveries.Pending => (
                "processing.outcome-delayed",
                $"Deluno could not tell {record.ProcessorName} what became of {record.ReleaseName} yet, and will keep trying. {record.ProcessorSaid}".TrimEnd()),
            _ => (
                "processing.outcome-not-reported",
                $"Deluno could not tell {record.ProcessorName} what became of {record.ReleaseName}. {record.ProcessorSaid}".TrimEnd())
        };
    }

    /// <summary>
    /// The processor's reply with any sentence that contradicts this outcome left out: a reply to "it has" that also
    /// says "will not import", or a reply to "will not import" that also says it was imported, is the processor
    /// repeating its note from another send of the release (#1232).
    /// </summary>
    internal static string? OwnWords(string? said, bool imported)
    {
        if (string.IsNullOrWhiteSpace(said)) return said;
        var sentences = System.Text.RegularExpressions.Regex.Split(said.Trim(), @"(?<=[.!?])\s+")
            .Where(sentence => !string.IsNullOrWhiteSpace(sentence))
            .Where(sentence => imported
                ? !System.Text.RegularExpressions.Regex.IsMatch(sentence, @"will not (be )?import|not imported|did not import", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                : !System.Text.RegularExpressions.Regex.IsMatch(sentence, @"(?<!not )(?<!not be )\bimported (the|this|it)\b|\bhas imported\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .ToArray();
        return sentences.Length == 0 ? null : string.Join(" ", sentences);
    }
}
