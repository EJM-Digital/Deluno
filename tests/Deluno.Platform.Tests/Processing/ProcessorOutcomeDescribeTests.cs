using Deluno.Platform.Contracts;

namespace Deluno.Platform.Tests.Processing;

/// <summary>
/// #652: the outcome Deluno tells a processor - and the same sentence a
/// person reads in Activity - has to say which pack files were skipped and
/// why, not only that the pack as a whole was imported.
/// </summary>
public sealed class ProcessorOutcomeDescribeTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-25T09:00:00Z");

    [Fact]
    public void An_imported_outcome_with_skipped_files_says_which_ones_and_why()
    {
        var record = new ProcessorOutcomeRecord(
            HandoffId: "h-1",
            ProcessorName: "Weir",
            LibraryId: "lib-1",
            ReleaseName: "The Last of Us S01",
            Outcome: ProcessorOutcomeRecord.Imported,
            ImportedPath: @"D:\tv\The Last of Us\Season 01",
            Reason: "Skipped 1 file(s) that do not resolve to a canonical episode: The Last of Us - S01E00 - Making of the Last of Us.mkv.",
            OccurredUtc: Now,
            Delivery: ProcessorOutcomeDeliveries.Delivered,
            Attempts: 1,
            LastAttemptUtc: Now,
            NextAttemptUtc: null,
            Released: true,
            ProcessorSaid: null,
            RecordedUtc: Now);

        var (kind, message) = Deluno.Platform.ProcessorOutcomeDelivery.Describe(record);

        Assert.Equal("processing.outcome-reported", kind);
        Assert.Contains("Deluno told Weir it has The Last of Us S01", message, StringComparison.Ordinal);
        Assert.Contains("Skipped 1 file(s) that do not resolve to a canonical episode", message, StringComparison.Ordinal);
        Assert.Contains("Making of the Last of Us.mkv", message, StringComparison.Ordinal);
        Assert.Contains("It released its copy.", message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_imported_outcome_with_nothing_skipped_reads_as_before()
    {
        var record = new ProcessorOutcomeRecord(
            HandoffId: "h-2",
            ProcessorName: "Weir",
            LibraryId: "lib-1",
            ReleaseName: "Dune Part Two",
            Outcome: ProcessorOutcomeRecord.Imported,
            ImportedPath: @"D:\movies\Dune Part Two\Dune Part Two.mkv",
            Reason: null,
            OccurredUtc: Now,
            Delivery: ProcessorOutcomeDeliveries.Delivered,
            Attempts: 1,
            LastAttemptUtc: Now,
            NextAttemptUtc: null,
            Released: true,
            ProcessorSaid: null,
            RecordedUtc: Now);

        var (_, message) = Deluno.Platform.ProcessorOutcomeDelivery.Describe(record);

        Assert.Equal("Deluno told Weir it has Dune Part Two. It released its copy.", message);
    }

    private static ProcessorOutcomeRecord Record(string outcome, string delivery, string? reason, string? processorSaid, bool released = false)
        => new(
            HandoffId: "h-9", ProcessorName: "Weir", LibraryId: "lib-1", ReleaseName: "Example Film (2024)", Outcome: outcome,
            ImportedPath: null, Reason: reason, OccurredUtc: Now, Delivery: delivery, Attempts: 1, LastAttemptUtc: Now, NextAttemptUtc: null,
            Released: released, ProcessorSaid: processorSaid, RecordedUtc: Now);

    [Fact]
    public void The_reported_the_not_reported_and_the_superseded_endings_each_read_in_their_own_plain_words()
    {
        var (reportedKind, reported) = Deluno.Platform.ProcessorOutcomeDelivery.Describe(
            Record(ProcessorOutcomeRecord.NotImported, ProcessorOutcomeDeliveries.Delivered, "The file is damaged.", null, released: true));
        var (notReportedKind, notReported) = Deluno.Platform.ProcessorOutcomeDelivery.Describe(
            Record(ProcessorOutcomeRecord.Imported, ProcessorOutcomeDeliveries.Refused, null, "It has no record of that hand-off."));
        var (supersededKind, superseded) = Deluno.Platform.ProcessorOutcomeDelivery.Describe(
            Record(ProcessorOutcomeRecord.Imported, ProcessorOutcomeDeliveries.Replaced, null, null));

        Assert.Equal("processing.outcome-reported", reportedKind);
        Assert.Equal("Deluno told Weir it will not import Example Film (2024): The file is damaged. It released its copy.", reported);
        Assert.Equal("processing.outcome-not-reported", notReportedKind);
        Assert.Equal("Deluno could not tell Weir what became of Example Film (2024). It has no record of that hand-off.", notReported);
        Assert.Equal("processing.outcome-superseded", supersededKind);
        Assert.Equal("Example Film (2024) was sent to Weir again after this send, so Deluno left the report to the newer send, which is the one that is handled.", superseded);
    }

    [Fact]
    public void No_ending_reads_with_a_doubled_full_stop_a_doubled_space_or_a_trailing_space()
    {
        var deliveries = typeof(ProcessorOutcomeDeliveries).GetFields()
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToArray();
        Assert.True(deliveries.Length >= 6, "the endings should include pending, delivered, refused, settled, never received and replaced");

        foreach (var delivery in deliveries)
        {
            foreach (var outcome in new[] { ProcessorOutcomeRecord.Imported, ProcessorOutcomeRecord.NotImported })
            {
                // The reason and the processor's words arrive with and without their own full stop.
                foreach (var reason in new[] { "Skipped one file.", "Skipped one file", null })
                {
                    foreach (var said in new[] { "It kept its copy.", null })
                    {
                        var (_, message) = Deluno.Platform.ProcessorOutcomeDelivery.Describe(
                            outcome == ProcessorOutcomeRecord.NotImported && reason is null
                                ? Record(outcome, delivery, "Not wanted", said)
                                : Record(outcome, delivery, reason, said));
                        var context = $"{delivery}/{outcome}/{reason}/{said}: {message}";
                        Assert.DoesNotContain("..", message, StringComparison.Ordinal);
                        Assert.DoesNotContain("  ", message, StringComparison.Ordinal);
                        Assert.False(message.EndsWith(' '), context);
                        Assert.True(message.EndsWith('.') || message.EndsWith(')') || message.EndsWith('?'), context);
                    }
                }
            }
        }
    }
}
