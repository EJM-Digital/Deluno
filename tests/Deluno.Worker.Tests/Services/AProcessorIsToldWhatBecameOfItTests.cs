using Deluno.Filesystem;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Platform;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Runtime.CompilerServices;

namespace Deluno.Worker.Tests.Services;

/// <summary>
/// A processor is told what became of the file it handed back (#578), and is
/// told again until it has heard (#580).
///
/// <para><b>Why this file exists.</b> A processor cannot know. Deluno's answer
/// to a completed report only says an import was queued, and a queued import
/// can still fail — so the processor keeps its copy and nothing ever tells it
/// otherwise, and its output folder only grows. Deluno reaching in to tidy that
/// folder would be the wrong side of the boundary: a processor serves media
/// managers that cannot talk to it at all, so cleaning up after itself has to
/// be its own job. What Deluno owes is the fact.</para>
///
/// <para>Said once, one blip lost the fact for good. On the rig Weir 3.2.0
/// refused one with a 409 for a hand-off it had itself just completed, and
/// Deluno recorded the refusal and never said it again.</para>
/// </summary>
public sealed class AProcessorIsToldWhatBecameOfItTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-23T07:46:41Z");

    private readonly Mock<IProcessorRepository> _processors = new();
    private readonly Mock<IProcessorConnectionService> _connections = new();
    private readonly Mock<IActivityFeedRepository> _activity = new();
    private readonly Dictionary<string, ProcessorOutcomeRecord> _owed = new();
    private readonly List<(string Kind, string Message)> _said = [];
    private readonly List<DateTimeOffset> _sentOccurred = [];
    private readonly Clock _clock = new(Start);
    private ProcessorConnectionItem? _connection;

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
        _connection = Blank<ProcessorConnectionItem>() with { Id = "c1", Name = "Weir", IsEnabled = true };
        _processors.Setup(repo => repo.FindProcessorConnectionAsync(It.IsAny<string?>(), "Weir", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _connection);

        // What is owed lives in a store the tests can read, as it does in SQLite.
        _processors.Setup(repo => repo.SaveProcessorOutcomeAsync(It.IsAny<ProcessorOutcomeRecord>(), It.IsAny<CancellationToken>()))
            .Callback<ProcessorOutcomeRecord, CancellationToken>((record, _) => _owed[record.HandoffId] = record)
            .Returns(Task.CompletedTask);
        _processors.Setup(repo => repo.ListDueProcessorOutcomesAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTimeOffset now, CancellationToken _) => (IReadOnlyList<ProcessorOutcomeRecord>)_owed.Values
                .Where(record => record.Delivery == ProcessorOutcomeDeliveries.Pending && (record.NextAttemptUtc is null || record.NextAttemptUtc <= now))
                .ToArray());

        _activity.Setup(feed => feed.RecordActivityAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string?, string?, string?, string?, CancellationToken>(
                (kind, said, _, _, _, _, _) => _said.Add((kind, said)))
            .ReturnsAsync((ActivityEventItem)null!);

        CanBeTold(true);
        Answers(new ProcessorOutcomeResult(ProcessorAnswer.Answered, true, null));
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
                Start,
                It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.Equal(ProcessorOutcomeDeliveries.Delivered, Owed().Delivery);
    }

    /// <summary>
    /// The path that has to work for every other media manager: a processor
    /// that does not advertise the capability is told nothing and keeps its
    /// copy until its own retention reclaims it. Written down so the drawer can
    /// say so, never retried and never announced.
    /// </summary>
    [Fact]
    public async Task A_processor_that_does_not_advertise_it_is_told_nothing()
    {
        CanBeTold(false);

        await Reporter().ReportAsync("handoff-1", ProcessorHandoffOutcome.Imported, null, null, CancellationToken.None);

        _connections.Verify(service => service.ReportOutcomeAsync(
                It.IsAny<ProcessorConnectionItem>(), It.IsAny<string>(), It.IsAny<ProcessorHandoffOutcome>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()),
            Times.Never);
        Assert.Equal(ProcessorOutcomeDeliveries.GivenUp, Owed().Delivery);
        Assert.Empty(_said);
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
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("gone"));

        await Reporter().ReportAsync("handoff-1", ProcessorHandoffOutcome.Imported, null, null, CancellationToken.None);
    }

    [Fact]
    public async Task What_the_processor_said_is_recorded_where_a_person_can_read_it()
    {
        Answers(new ProcessorOutcomeResult(ProcessorAnswer.Answered, false, "The import had already moved it."));

        await Reporter().ReportAsync("handoff-1", ProcessorHandoffOutcome.Imported, null, null, CancellationToken.None);

        var (kind, message) = Assert.Single(_said);
        Assert.Equal("processing.outcome-reported", kind);
        Assert.Contains("The import had already moved it.", message);
    }

    /// <summary>
    /// The rig's case: Weir 3.2.0 answered 409 "This hand-off ended failed" for
    /// a hand-off it had just completed. That is not final, and Weir 3.2.1
    /// answers the same report 200.
    /// </summary>
    [Fact]
    public async Task An_outcome_that_does_not_land_is_said_again_until_it_does()
    {
        Answers(new ProcessorOutcomeResult(ProcessorAnswer.Busy, false, "This hand-off ended failed, so Weir handed back no file to import."));
        await Reporter().ReportAsync("handoff-1", ProcessorHandoffOutcome.Imported, @"X:\Films\Tears of Steel (2012).mkv", null, CancellationToken.None);
        Assert.Equal(ProcessorOutcomeDeliveries.Pending, Owed().Delivery);

        // Not before it is due.
        _clock.Advance(TimeSpan.FromSeconds(30));
        await Reporter().SendDueAsync(CancellationToken.None);
        Assert.Equal(1, Owed().Attempts);

        Answers(new ProcessorOutcomeResult(ProcessorAnswer.Answered, false, "Nothing for Weir to remove."));
        _clock.Advance(TimeSpan.FromMinutes(1));
        await Reporter().SendDueAsync(CancellationToken.None);

        Assert.Equal(ProcessorOutcomeDeliveries.Delivered, Owed().Delivery);
        Assert.Equal(2, Owed().Attempts);
        // The same report both times: when it happened, not when it was said.
        Assert.Equal(new[] { Start, Start }, _sentOccurred);
    }

    /// <summary>
    /// A processor switched off or restarting at the moment the import
    /// finished used to be read as not wanting to know, and was told nothing.
    /// </summary>
    [Fact]
    public async Task A_processor_that_could_not_be_asked_at_the_time_is_still_owed()
    {
        _connections.Setup(service => service.GetStatusSupportAsync(It.IsAny<ProcessorConnectionItem>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessorStatusSupport(ProcessorAnswer.Unreachable, false, false, "Deluno could not reach Weir."));

        await Reporter().ReportAsync("handoff-1", ProcessorHandoffOutcome.Imported, null, null, CancellationToken.None);

        Assert.Equal(ProcessorOutcomeDeliveries.Pending, Owed().Delivery);
        Assert.Equal(Start + TimeSpan.FromMinutes(1), Owed().NextAttemptUtc);
    }

    [Theory]
    [InlineData(ProcessorAnswer.Answered, ProcessorOutcomeDeliveries.Delivered)]
    [InlineData(ProcessorAnswer.NotFound, ProcessorOutcomeDeliveries.NeverReceived)]
    [InlineData(ProcessorAnswer.Refused, ProcessorOutcomeDeliveries.Refused)]
    [InlineData(ProcessorAnswer.Unsupported, ProcessorOutcomeDeliveries.Refused)]
    [InlineData(ProcessorAnswer.Settled, ProcessorOutcomeDeliveries.Settled)]
    [InlineData(ProcessorAnswer.Busy, ProcessorOutcomeDeliveries.Pending)]
    [InlineData(ProcessorAnswer.Unreachable, ProcessorOutcomeDeliveries.Pending)]
    [InlineData(ProcessorAnswer.Forbidden, ProcessorOutcomeDeliveries.Pending)]
    public void Only_an_answer_no_repeat_can_change_ends_it(ProcessorAnswer answer, string delivery)
        => Assert.Equal(delivery, ProcessorOutcomeDeliveries.After(answer));

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(6, 32)]
    [InlineData(7, 60)]
    [InlineData(40, 60)]
    public void The_wait_doubles_from_a_minute_to_an_hour(int attempts, int minutes)
        => Assert.Equal(TimeSpan.FromMinutes(minutes), ProcessorOutcomeDeliveries.WaitAfter(attempts));

    /// <summary>
    /// The rig's Nosferatu: Weir recorded "will not import" when the import dead-lettered, and keeps that answer
    /// when the later import is reported. Deluno stops telling it, says so in one plain line, and never shows the
    /// red "could not tell" for it (#1155).
    /// </summary>
    [Fact]
    public async Task A_processor_that_keeps_its_earlier_answer_is_not_told_again_and_it_is_not_a_failure()
    {
        Answers(new ProcessorOutcomeResult(ProcessorAnswer.Settled, false, "Deluno already said it will not import this file, so Weir kept that answer."));

        await Reporter().ReportAsync("handoff-1", ProcessorHandoffOutcome.Imported, "/films/Nosferatu (1922).mkv", null, CancellationToken.None);

        Assert.Equal(ProcessorOutcomeDeliveries.Settled, Owed().Delivery);
        Assert.Null(Owed().NextAttemptUtc);
        var (kind, message) = Assert.Single(_said);
        Assert.Equal("processing.outcome-kept", kind);
        Assert.Contains("kept its earlier answer", message);
        Assert.DoesNotContain("could not tell", message, StringComparison.OrdinalIgnoreCase);

        _clock.Advance(TimeSpan.FromHours(2));
        await Reporter().SendDueAsync(CancellationToken.None);
        Assert.Equal(1, Owed().Attempts);
        Assert.Single(_said);
    }

    /// <summary>
    /// A processor down for a day would otherwise write a line an hour into
    /// Activity. Said on the first miss and when it ends, and not between.
    /// </summary>
    [Fact]
    public async Task Trying_again_is_said_once_not_every_time()
    {
        Answers(new ProcessorOutcomeResult(ProcessorAnswer.Unreachable, false, "Deluno could not reach Weir."));
        await Reporter().ReportAsync("handoff-1", ProcessorHandoffOutcome.Imported, null, null, CancellationToken.None);
        for (var pass = 0; pass < 5; pass++)
        {
            _clock.Advance(TimeSpan.FromHours(1));
            await Reporter().SendDueAsync(CancellationToken.None);
        }

        Answers(new ProcessorOutcomeResult(ProcessorAnswer.Answered, true, null));
        _clock.Advance(TimeSpan.FromHours(1));
        await Reporter().SendDueAsync(CancellationToken.None);

        Assert.Equal(new[] { "processing.outcome-delayed", "processing.outcome-reported" }, _said.Select(item => item.Kind));
        Assert.Contains("on attempt 7", _said[^1].Message);
    }

    [Fact]
    public async Task After_a_week_unheard_Deluno_stops_and_says_so()
    {
        Answers(new ProcessorOutcomeResult(ProcessorAnswer.Unreachable, false, "Deluno could not reach Weir."));
        await Reporter().ReportAsync("handoff-1", ProcessorHandoffOutcome.Imported, null, null, CancellationToken.None);

        _clock.Advance(ProcessorOutcomeDeliveries.GiveUpAfter);
        await Reporter().SendDueAsync(CancellationToken.None);

        Assert.Equal(ProcessorOutcomeDeliveries.GivenUp, Owed().Delivery);
        Assert.Null(Owed().NextAttemptUtc);
        Assert.Equal("processing.outcome-not-reported", _said[^1].Kind);
        Assert.Contains("stopped trying", _said[^1].Message);
    }

    /// <summary>
    /// #1182: the rig's partial hand-off. Weir answers every report with a 409 that carries no "already recorded"
    /// code until its next release, so the report stays owed. It must keep being sent, backing off to once an
    /// hour, for the whole week, and not drop out after a few attempts.
    /// </summary>
    [Fact]
    public async Task A_report_the_processor_keeps_refusing_with_a_conflict_is_sent_every_hour_for_the_whole_week()
    {
        Answers(new ProcessorOutcomeResult(ProcessorAnswer.Busy, false, "This hand-off ended failed, so Weir handed back no file to import."));
        await Reporter().ReportAsync("handoff-1", ProcessorHandoffOutcome.Imported, "/films/Clown.mkv", "left one small extra out", CancellationToken.None);

        var end = Start + ProcessorOutcomeDeliveries.GiveUpAfter;
        var lastGap = TimeSpan.Zero;
        while (_clock.GetUtcNow() + TimeSpan.FromHours(1) < end)
        {
            var before = Owed().NextAttemptUtc!.Value;
            _clock.Advance(before - _clock.GetUtcNow());
            await Reporter().SendDueAsync(CancellationToken.None);

            Assert.Equal(ProcessorOutcomeDeliveries.Pending, Owed().Delivery);
            lastGap = Owed().NextAttemptUtc!.Value - _clock.GetUtcNow();
            Assert.True(lastGap <= TimeSpan.FromHours(1), "never waits longer than an hour between attempts");
        }

        // About 170 attempts in the week, not five; and the next one is at most an hour away.
        Assert.True(Owed().Attempts > 150, $"attempts: {Owed().Attempts}");
        Assert.Equal(TimeSpan.FromHours(1), lastGap);
        Assert.Single(_said, item => item.Kind == "processing.outcome-delayed");

        // Only when the week is over does it stop, and it says so.
        var due = Owed().NextAttemptUtc!.Value;
        _clock.Advance((due > end ? due : end) - _clock.GetUtcNow());
        await Reporter().SendDueAsync(CancellationToken.None);
        Assert.Equal(ProcessorOutcomeDeliveries.GivenUp, Owed().Delivery);
        Assert.Contains("stopped trying", _said[^1].Message);
    }

    [Fact]
    public async Task A_removed_connection_leaves_nobody_to_tell()
    {
        Answers(new ProcessorOutcomeResult(ProcessorAnswer.Unreachable, false, null));
        await Reporter().ReportAsync("handoff-1", ProcessorHandoffOutcome.Imported, null, null, CancellationToken.None);

        _connection = null;
        _clock.Advance(TimeSpan.FromMinutes(1));
        await Reporter().SendDueAsync(CancellationToken.None);

        Assert.Equal(ProcessorOutcomeDeliveries.GivenUp, Owed().Delivery);
        Assert.Contains("was removed", Owed().ProcessorSaid);
    }

    /// <summary>Switched off in Deluno means not talking to it. It is still owed.</summary>
    [Fact]
    public async Task A_switched_off_processor_is_not_contacted_and_not_given_up_on()
    {
        _connection = _connection! with { IsEnabled = false };

        await Reporter().ReportAsync("handoff-1", ProcessorHandoffOutcome.Imported, null, null, CancellationToken.None);

        _connections.Verify(service => service.GetStatusSupportAsync(It.IsAny<ProcessorConnectionItem>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(ProcessorOutcomeDeliveries.Pending, Owed().Delivery);
        Assert.Equal(0, Owed().Attempts);
    }

    /// <summary>
    /// #1230: a hand-off the processor never received had nothing to tell it. Activity says so plainly, in a kind that
    /// is not a failure to report.
    /// </summary>
    [Fact]
    public async Task A_hand_off_the_processor_never_received_says_so_and_is_not_a_failure_to_report()
    {
        Answers(new ProcessorOutcomeResult(ProcessorAnswer.NotFound, false, "Weir on the server has no record of this hand-off."));

        await Reporter().ReportAsync("handoff-1", ProcessorHandoffOutcome.NotImported, null, "Deluno removed it.", CancellationToken.None);

        var (kind, message) = Assert.Single(_said);
        Assert.Equal("processing.outcome-never-received", kind);
        Assert.Equal("Weir never received this hand-off of Tears.of.Steel.2012.1080p-DELUNO, so there was nothing to tell it.", message);
        Assert.DoesNotContain("could not tell", message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Deluno.Contracts.ActivityHistory.Outcomes.Good, Deluno.Contracts.ActivityHistory.OutcomeOf(kind));
        Assert.Null(Owed().NextAttemptUtc);
    }

    /// <summary>
    /// A person can say it again once they have fixed what stopped it — and
    /// the week begins again.
    /// </summary>
    [Fact]
    public async Task A_person_can_have_it_said_again_from_the_start()
    {
        Answers(new ProcessorOutcomeResult(ProcessorAnswer.NotFound, false, "Weir has no record of this hand-off."));
        await Reporter().ReportAsync("handoff-1", ProcessorHandoffOutcome.Imported, null, null, CancellationToken.None);
        Assert.Equal(ProcessorOutcomeDeliveries.NeverReceived, Owed().Delivery);

        Answers(new ProcessorOutcomeResult(ProcessorAnswer.Answered, false, "Nothing for Weir to remove."));
        _clock.Advance(TimeSpan.FromDays(10));
        var sent = await ProcessorOutcomeDelivery.SendAgainAsync(
            _processors.Object, _connections.Object, _activity.Object, Owed(), _clock.GetUtcNow(), CancellationToken.None);

        Assert.Equal(ProcessorOutcomeDeliveries.Delivered, sent.Delivery);
        Assert.Equal(1, sent.Attempts);
        Assert.Equal(Start, sent.OccurredUtc);
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
    // A lock or a file still arriving (#603): I/O is not a refusal.
    [InlineData(503, 1, 3, false)]
    [InlineData(409, 2, 3, false)]
    // Transient, and this is the last attempt, so nothing more is coming.
    [InlineData(500, 3, 3, true)]
    public void A_failure_is_only_final_when_Deluno_will_not_try_again(int statusCode, int attempts, int maxAttempts, bool final)
    {
        var result = new ImportPipelineResult(false, statusCode, null, "no");
        var job = Blank<JobQueueItem>() with { Attempts = attempts, MaxAttempts = maxAttempts };

        Assert.Equal(final, ProcessorOutcomeReporter.WillNotTryAgain(result, job));
    }

    private ProcessorOutcomeRecord Owed() => _owed["handoff-1"];

    private void CanBeTold(bool told)
        => _connections.Setup(service => service.GetStatusSupportAsync(It.IsAny<ProcessorConnectionItem>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessorStatusSupport(ProcessorAnswer.Answered, true, true, null) { CanBeToldTheOutcome = told });

    private void Answers(ProcessorOutcomeResult result)
        => _connections.Setup(service => service.ReportOutcomeAsync(
                It.IsAny<ProcessorConnectionItem>(), It.IsAny<string>(), It.IsAny<ProcessorHandoffOutcome>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Callback<ProcessorConnectionItem, string, ProcessorHandoffOutcome, string?, string?, DateTimeOffset, CancellationToken>(
                (_, _, _, _, _, occurred, _) => _sentOccurred.Add(occurred))
            .ReturnsAsync(result);

    private ProcessorOutcomeReporter Reporter()
        => new(_processors.Object, _connections.Object, _activity.Object, _clock, NullLogger<ProcessorOutcomeReporter>.Instance);

    private static T Blank<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
