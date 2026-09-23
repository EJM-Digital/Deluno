using Deluno.Filesystem;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Runtime.CompilerServices;

namespace Deluno.Worker.Tests.Services;

/// <summary>
/// A processor is told what became of the file it handed back (#578).
///
/// <para><b>Why this file exists.</b> A processor cannot know. Deluno's answer
/// to a completed report only says an import was queued, and a queued import
/// can still fail — so the processor keeps its copy and nothing ever tells it
/// otherwise, and its output folder only grows. Deluno reaching in to tidy that
/// folder would be the wrong side of the boundary: a processor serves media
/// managers that cannot talk to it at all, so cleaning up after itself has to
/// be its own job. What Deluno owes is the fact.</para>
/// </summary>
public sealed class AProcessorIsToldWhatBecameOfItTests
{
    private readonly Mock<IProcessorRepository> _processors = new();
    private readonly Mock<IProcessorConnectionService> _connections = new();
    private readonly Mock<IActivityFeedRepository> _activity = new();

    public AProcessorIsToldWhatBecameOfItTests()
    {
        _processors.Setup(repo => repo.GetProcessorHandoffAsync("handoff-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Blank<ProcessorHandoffItem>() with
            {
                Id = "handoff-1",
                LibraryId = "library-1",
                ReleaseName = "Tears.of.Steel.2012.1080p-DELUNO",
                ProcessorName = "Weir"
            });
        _processors.Setup(repo => repo.FindProcessorConnectionByNameAsync("Weir", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Blank<ProcessorConnectionItem>() with { Id = "c1", Name = "Weir", IsEnabled = true });
        CanBeTold(true);
        _connections.Setup(service => service.ReportOutcomeAsync(
                It.IsAny<ProcessorConnectionItem>(), It.IsAny<string>(), It.IsAny<ProcessorHandoffOutcome>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessorOutcomeResult(ProcessorAnswer.Answered, true, null));
    }

    [Fact]
    public async Task A_processor_that_wants_to_know_is_told_the_file_was_imported()
    {
        await Reporter().ReportAsync("handoff-1", ProcessorHandoffOutcome.Imported, @"X:\Films\Tears of Steel (2012).mkv", null, CancellationToken.None);

        _connections.Verify(service => service.ReportOutcomeAsync(
                It.IsAny<ProcessorConnectionItem>(),
                "handoff-1",
                ProcessorHandoffOutcome.Imported,
                @"X:\Films\Tears of Steel (2012).mkv",
                null,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// The path that has to work for every other media manager: a processor
    /// that does not advertise the capability is told nothing and keeps its
    /// copy until its own retention reclaims it.
    /// </summary>
    [Fact]
    public async Task A_processor_that_does_not_advertise_it_is_told_nothing()
    {
        CanBeTold(false);

        await Reporter().ReportAsync("handoff-1", ProcessorHandoffOutcome.Imported, null, null, CancellationToken.None);

        _connections.Verify(service => service.ReportOutcomeAsync(
                It.IsAny<ProcessorConnectionItem>(), It.IsAny<string>(), It.IsAny<ProcessorHandoffOutcome>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// A processor disagreeing about its own bookkeeping is not a reason to
    /// undo an import that has already happened.
    /// </summary>
    [Fact]
    public async Task A_processor_that_refuses_the_answer_does_not_fail_the_import()
    {
        _connections.Setup(service => service.ReportOutcomeAsync(
                It.IsAny<ProcessorConnectionItem>(), It.IsAny<string>(), It.IsAny<ProcessorHandoffOutcome>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("gone"));

        await Reporter().ReportAsync("handoff-1", ProcessorHandoffOutcome.Imported, null, null, CancellationToken.None);
    }

    [Fact]
    public async Task What_the_processor_said_is_recorded_where_a_person_can_read_it()
    {
        _connections.Setup(service => service.ReportOutcomeAsync(
                It.IsAny<ProcessorConnectionItem>(), It.IsAny<string>(), It.IsAny<ProcessorHandoffOutcome>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessorOutcomeResult(ProcessorAnswer.Answered, false, "The import had already moved it."));
        string? category = null, message = null;
        _activity.Setup(feed => feed.RecordActivityAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string?, string?, string?, string?, CancellationToken>(
                (kind, said, _, _, _, _, _) => (category, message) = (kind, said))
            .ReturnsAsync((ActivityEventItem)null!);

        await Reporter().ReportAsync("handoff-1", ProcessorHandoffOutcome.Imported, null, null, CancellationToken.None);

        Assert.Equal("processing.outcome-reported", category);
        Assert.Contains("The import had already moved it.", message);
    }

    /// <summary>
    /// The rule that matters most: "not imported" is permission to delete, so a
    /// failure Deluno will try again must stay silent or the retry finds
    /// nothing.
    /// </summary>
    [Theory]
    // The file itself is wrong, so a second attempt reads the same bytes.
    [InlineData(400, 1, 3, true)]
    // Transient, with attempts left.
    [InlineData(500, 1, 3, false)]
    [InlineData(409, 2, 3, false)]
    // Transient, and this is the last attempt, so nothing more is coming.
    [InlineData(500, 3, 3, true)]
    public void A_failure_is_only_final_when_Deluno_will_not_try_again(int statusCode, int attempts, int maxAttempts, bool final)
    {
        var result = new ImportPipelineResult(false, statusCode, null, "no");
        var job = Blank<JobQueueItem>() with { Attempts = attempts, MaxAttempts = maxAttempts };

        Assert.Equal(final, ProcessorOutcomeReporter.WillNotTryAgain(result, job));
    }

    private void CanBeTold(bool told)
        => _connections.Setup(service => service.GetStatusSupportAsync(It.IsAny<ProcessorConnectionItem>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessorStatusSupport(ProcessorAnswer.Answered, true, true, null) { CanBeToldTheOutcome = told });

    private ProcessorOutcomeReporter Reporter()
        => new(_processors.Object, _connections.Object, _activity.Object, NullLogger<ProcessorOutcomeReporter>.Instance);

    private static T Blank<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
}
