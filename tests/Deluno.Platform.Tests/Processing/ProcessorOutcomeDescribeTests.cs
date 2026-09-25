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
}
