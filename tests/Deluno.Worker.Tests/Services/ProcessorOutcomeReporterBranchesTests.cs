using System.Runtime.CompilerServices;
using System.Text.Json;
using Deluno.Filesystem;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Worker.Services;
using Deluno.Worker.Tests.Support;
using Microsoft.Extensions.Logging;
using Moq;

namespace Deluno.Worker.Tests.Services;

/// <summary>#578, #580, #1226: what the reporter does when told what became of a file, settles from an import, sends again what is owed, and what it says when it cannot.</summary>
public sealed class ProcessorOutcomeReporterBranchesTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-20T10:00:00Z");

    private readonly Mock<IProcessorRepository> _processors = new();
    private readonly Mock<IActivityFeedRepository> _activity = new();
    private readonly RecordingLogger<ProcessorOutcomeReporter> _log = new();

    private ProcessorOutcomeReporter Reporter()
        => new(_processors.Object, new Mock<IProcessorConnectionService>().Object, _activity.Object, new FixedClock(Now), _log);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static ProcessorHandoffItem Handoff(string status, string? processor = "Weir")
        => new("h1", "lib-1", "movie", "client-1", "q1", "Heat.1995", "/d/Heat.1995", processor, status, null, null, null, Now, Now);

    private static readonly string Waiting = ProcessorHandoffStatuses.AcceptingLateOutput[0];

    [Fact]
    public async Task A_report_for_a_hand_off_that_is_not_known_does_nothing()
    {
        _processors.Setup(p => p.GetProcessorHandoffAsync("h1", It.IsAny<CancellationToken>())).ReturnsAsync((ProcessorHandoffItem?)null);

        await Reporter().ReportAsync("h1", ProcessorHandoffOutcome.Imported, "/lib/x.mkv", null, CancellationToken.None);

        Assert.Empty(_log.Entries);
        _activity.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_report_that_cannot_be_written_never_throws_and_says_which_hand_off_in_the_log()
    {
        var failure = new IOException("db busy");
        _processors.Setup(p => p.GetProcessorHandoffAsync("h1", It.IsAny<CancellationToken>())).ThrowsAsync(failure);

        await Reporter().ReportAsync("h1", ProcessorHandoffOutcome.Imported, null, null, CancellationToken.None);

        var entry = Assert.Single(_log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("Could not tell the processor what became of hand-off h1.", entry.Message);
        Assert.Same(failure, entry.Exception);
    }

    [Fact]
    public async Task A_stop_while_reporting_is_not_swallowed()
    {
        _processors.Setup(p => p.GetProcessorHandoffAsync("h1", It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(() => Reporter().ReportAsync("h1", ProcessorHandoffOutcome.Imported, null, null, CancellationToken.None));
    }

    [Theory]
    [InlineData(null, "/out/x.mkv")]
    [InlineData("  ", "/out/x.mkv")]
    public async Task An_import_with_no_library_or_no_source_settles_nothing(string? from, string output)
    {
        Assert.Null(await Reporter().SettleFromImportAsync("lib-1", from, output, "job-1", CancellationToken.None));
        Assert.Null(await Reporter().SettleFromImportAsync(" ", "/d/Heat.1995", output, "job-1", CancellationToken.None));
        _processors.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task An_import_no_hand_off_holds_the_source_of_settles_nothing()
    {
        _processors.Setup(p => p.FindProcessorHandoffAsync("lib-1", null, "/d/Heat.1995", It.IsAny<CancellationToken>())).ReturnsAsync((ProcessorHandoffItem?)null);

        Assert.Null(await Reporter().SettleFromImportAsync("lib-1", "/d/Heat.1995", "/out/x.mkv", "job-1", CancellationToken.None));
    }

    [Fact]
    public async Task A_hand_off_that_already_ended_another_way_is_left_alone_and_returned()
    {
        var ended = Handoff(ProcessorHandoffStatuses.Completed);
        _processors.Setup(p => p.FindProcessorHandoffAsync("lib-1", null, "/d/Heat.1995", It.IsAny<CancellationToken>())).ReturnsAsync(ended);

        var result = await Reporter().SettleFromImportAsync("lib-1", "/d/Heat.1995", "/out/x.mkv", "job-1", CancellationToken.None);

        Assert.Same(ended, result);
        _processors.Verify(p => p.UpdateProcessorHandoffIfStatusAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_report_that_moved_the_hand_off_first_wins_and_the_fresh_state_is_returned_or_the_old_one(bool freshKnown)
    {
        var live = Handoff(Waiting);
        var fresh = Handoff(ProcessorHandoffStatuses.Failed);
        _processors.Setup(p => p.FindProcessorHandoffAsync("lib-1", null, "/d/Heat.1995", It.IsAny<CancellationToken>())).ReturnsAsync(live);
        _processors.Setup(p => p.UpdateProcessorHandoffIfStatusAsync("h1", Waiting, ProcessorHandoffStatuses.Completed, "/out/x.mkv", "job-1", null, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _processors.Setup(p => p.GetProcessorHandoffAsync("h1", It.IsAny<CancellationToken>())).ReturnsAsync(freshKnown ? fresh : null);

        var result = await Reporter().SettleFromImportAsync("lib-1", "/d/Heat.1995", "/out/x.mkv", "job-1", CancellationToken.None);

        Assert.Same(freshKnown ? fresh : live, result);
        _activity.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("Weir", "Weir's cleaned copy")]
    [InlineData(null, "the processor's cleaned copy")]
    [InlineData("  ", "the processor's cleaned copy")]
    public async Task An_import_from_a_waiting_hand_off_completes_it_and_writes_one_line_in_plain_words(string? processor, string copyWords)
    {
        var live = Handoff(Waiting, processor);
        var settled = Handoff(ProcessorHandoffStatuses.Completed, processor);
        _processors.Setup(p => p.FindProcessorHandoffAsync("lib-1", null, "/d/Heat.1995", It.IsAny<CancellationToken>())).ReturnsAsync(live);
        _processors.Setup(p => p.UpdateProcessorHandoffIfStatusAsync("h1", Waiting, ProcessorHandoffStatuses.Completed, "/out/x.mkv", "job-1", null, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _processors.Setup(p => p.GetProcessorHandoffAsync("h1", It.IsAny<CancellationToken>())).ReturnsAsync(settled);
        string? details = null;
        _activity.Setup(a => a.RecordActivityAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string?, string?, string?, string?, CancellationToken>((_, _, d, _, _, _, _) => details = d)
            .ReturnsAsync((ActivityEventItem)null!);

        var result = await Reporter().SettleFromImportAsync("lib-1", "/d/Heat.1995", "/out/x.mkv", "job-1", CancellationToken.None);

        Assert.Same(settled, result);
        _activity.Verify(a => a.RecordActivityAsync(
            "processing.handoff.settled-by-import",
            $"Heat.1995 was imported from {copyWords}, so Deluno closed this hand-off as complete.",
            It.IsAny<string?>(), null, "processor-handoff", "h1", It.IsAny<CancellationToken>()), Times.Once);
        using var document = JsonDocument.Parse(details!);
        var root = document.RootElement;
        Assert.Equal("h1", root.GetProperty("HandoffId").GetString());
        Assert.Equal("lib-1", root.GetProperty("LibraryId").GetString());
        Assert.Equal("Heat.1995", root.GetProperty("ReleaseName").GetString());
        Assert.Equal(Waiting, root.GetProperty("PreviousStatus").GetString());
        Assert.Equal("/out/x.mkv", root.GetProperty("OutputPath").GetString());
        Assert.Equal("job-1", root.GetProperty("JobId").GetString());
    }

    [Fact]
    public async Task A_settled_hand_off_that_can_no_longer_be_read_returns_the_one_that_was_live()
    {
        var live = Handoff(Waiting);
        _processors.Setup(p => p.FindProcessorHandoffAsync("lib-1", null, "/d/Heat.1995", It.IsAny<CancellationToken>())).ReturnsAsync(live);
        _processors.Setup(p => p.UpdateProcessorHandoffIfStatusAsync("h1", Waiting, ProcessorHandoffStatuses.Completed, "/out/x.mkv", "job-1", null, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _processors.Setup(p => p.GetProcessorHandoffAsync("h1", It.IsAny<CancellationToken>())).ReturnsAsync((ProcessorHandoffItem?)null);

        Assert.Same(live, await Reporter().SettleFromImportAsync("lib-1", "/d/Heat.1995", "/out/x.mkv", "job-1", CancellationToken.None));
    }

    [Fact]
    public async Task A_settle_that_fails_is_warned_with_the_output_path_and_returns_nothing_while_a_stop_goes_on()
    {
        var failure = new IOException("db busy");
        _processors.Setup(p => p.FindProcessorHandoffAsync("lib-1", null, "/d/Heat.1995", It.IsAny<CancellationToken>())).ThrowsAsync(failure);

        Assert.Null(await Reporter().SettleFromImportAsync("lib-1", "/d/Heat.1995", "/out/x.mkv", "job-1", CancellationToken.None));

        var entry = Assert.Single(_log.Entries);
        Assert.Equal("Could not settle the hand-off for an imported file at /out/x.mkv.", entry.Message);
        Assert.Same(failure, entry.Exception);

        _processors.Setup(p => p.FindProcessorHandoffAsync("lib-1", null, "/d/Heat.1995", It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException());
        await Assert.ThrowsAsync<OperationCanceledException>(() => Reporter().SettleFromImportAsync("lib-1", "/d/Heat.1995", "/out/x.mkv", "job-1", CancellationToken.None));
    }

    private static ProcessorOutcomeRecord Owed(string handoffId)
        => ((ProcessorOutcomeRecord)RuntimeHelpers.GetUninitializedObject(typeof(ProcessorOutcomeRecord))) with { HandoffId = handoffId };

    [Fact]
    public async Task Nothing_owed_sends_nothing()
    {
        _processors.Setup(p => p.ListDueProcessorOutcomesAsync(Now, It.IsAny<CancellationToken>())).ReturnsAsync([]);

        await Reporter().SendDueAsync(CancellationToken.None);

        Assert.Empty(_log.Entries);
    }

    [Fact]
    public async Task One_report_that_cannot_be_sent_is_warned_about_by_hand_off_and_the_rest_are_still_tried()
    {
        _processors.Setup(p => p.ListDueProcessorOutcomesAsync(Now, It.IsAny<CancellationToken>())).ReturnsAsync([Owed("h1"), Owed("h2")]);
        _processors.Setup(p => p.FindProcessorConnectionAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("db busy"));

        await Reporter().SendDueAsync(CancellationToken.None);

        Assert.Equal(
            ["Could not tell the processor what became of hand-off h1.", "Could not tell the processor what became of hand-off h2."],
            _log.Entries.Select(entry => entry.Message));
    }

    [Fact]
    public async Task A_stop_while_sending_what_is_owed_is_not_swallowed()
    {
        _processors.Setup(p => p.ListDueProcessorOutcomesAsync(Now, It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(() => Reporter().SendDueAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(400, null, 1, 3, true)]
    [InlineData(500, "replacementRejected", 1, 3, true)]
    [InlineData(500, null, 3, 3, true)]
    [InlineData(500, null, 4, 3, true)]
    [InlineData(500, null, 2, 3, false)]
    [InlineData(409, "other", 1, 3, false)]
    public void A_failed_import_is_final_when_refused_or_out_of_attempts(int status, string? kind, int attempts, int max, bool final)
    {
        var result = new ImportPipelineResult(false, status, null, "no", kind);
        var job = TestJobs.Create("filesystem.import.execute") with { Attempts = attempts, MaxAttempts = max };

        Assert.Equal(final, ProcessorOutcomeReporter.WillNotTryAgain(result, job));
        Assert.Equal(status == 400 || kind == "replacementRejected", ProcessorOutcomeReporter.IsRefusal(result));
    }
}
