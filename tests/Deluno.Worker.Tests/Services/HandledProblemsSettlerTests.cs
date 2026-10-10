using Deluno.Contracts;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Movies.Data;
using Deluno.Series.Data;
using Deluno.Worker.Services;
using Moq;

namespace Deluno.Worker.Tests.Services;

/// <summary>
/// #1167: a problem whose title has its file now closes itself, with one Activity line each. The rig's Nosferatu
/// kept a Deluge dispatch whose import had failed, long after the film was imported another way.
/// </summary>
public sealed class HandledProblemsSettlerTests
{
    private static readonly DateTimeOffset Created = DateTimeOffset.Parse("2026-10-07T10:00:00Z");

    private readonly Mock<IMovieCatalogRepository> _movies = new();
    private readonly Mock<ISeriesCatalogRepository> _series = new();
    private readonly Mock<IJobQueueRepository> _jobs = new();
    private readonly Mock<IDownloadDispatchesRepository> _dispatches = new();
    private readonly Mock<IActivityFeedRepository> _activity = new();
    private readonly List<(string Category, string Message)> _said = [];

    public HandledProblemsSettlerTests()
    {
        _movies.Setup(repo => repo.SettleCasesOfTitlesHoldingFilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _series.Setup(repo => repo.SettleCasesOfTitlesHoldingFilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _movies.Setup(repo => repo.GetTitleFileHoldsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(TitleFileHolds.None);
        _series.Setup(repo => repo.GetTitleFileHoldsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(TitleFileHolds.None);
        _activity.Setup(feed => feed.RecordActivityAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string?, string?, string?, string?, CancellationToken>((kind, said, _, _, _, _, _) => _said.Add((kind, said)))
            .ReturnsAsync((ActivityEventItem)null!);
    }

    private HandledProblemsSettler Settler() => new(_movies.Object, _series.Object, _jobs.Object, _dispatches.Object, _activity.Object);

    private static DownloadDispatchItem FailedImport(string id, string entityType, string entityId)
        => new(id, "lib", "movies", entityType, entityId, "Nosferatu.1922.1080p.BluRay.x264-HD4U", "PublicHD", "client-1", "Deluge",
            "completed", null, Created, "succeeded", Created, 200, null, null, null, Created, "hash", 1, "failed", Created, Created, null,
            "importFailed", "Deluno import failed: The destination file already exists.", null);

    private void Listed(params DownloadDispatchItem[] items)
        => _jobs.Setup(repo => repo.ListDownloadDispatchesAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<string?>()))
            .ReturnsAsync(items);

    private static TitleFileHolds Holds(string id, DateTimeOffset? imported)
        => new(new Dictionary<string, DateTimeOffset?> { [id] = imported });

    private static Deluno.Platform.Contracts.ProcessorHandoffItem Handoff(string status, string? failure)
        => new("h-1", "lib", "movies", "client-1", "hash", "Nosferatu.1922.1080p.BluRay.x264-HD4U", "/downloads/x", "Weir", status, null, null, failure, Created, Created);

    private HandledProblemsSettler SettlerWithProcessors(Mock<Deluno.Platform.Data.IProcessorRepository> processors)
        => new(_movies.Object, _series.Object, _jobs.Object, _dispatches.Object, _activity.Object, processors.Object);

    private static Mock<Deluno.Platform.Data.IProcessorRepository> WithSourceGone()
    {
        var processors = new Mock<Deluno.Platform.Data.IProcessorRepository>();
        processors.Setup(repo => repo.ListProcessorHandoffsWithStatusAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Handoff("source-gone", "The download was gone before Weir could clean it.")]);
        return processors;
    }

    // #1186
    [Fact]
    public async Task A_gone_download_row_closes_once_its_title_holds_the_file()
    {
        var processors = WithSourceGone();
        _dispatches.Setup(repo => repo.FindAnyDispatchByHashAsync("client-1", "hash", It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailedImport("d-9", "movie", "movie-1"));
        _movies.Setup(repo => repo.GetTitleFileHoldsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Holds("movie-1", Created.AddHours(2)));
        Listed();

        Assert.Equal(1, await SettlerWithProcessors(processors).SettleAsync(CancellationToken.None));

        processors.Verify(repo => repo.UpdateProcessorHandoffAsync("h-1", "dismissed", null, null, "The download was gone before Weir could clean it.", It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains(_said, line => line.Category == "processing.source-gone.settled" && line.Message.Contains("in the library already"));
    }

    [Fact]
    public async Task A_gone_download_row_closes_once_Deluno_has_removed_that_download()
    {
        var processors = WithSourceGone();
        _dispatches.Setup(repo => repo.FindAnyDispatchByHashAsync("client-1", "hash", It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailedImport("d-9", "movie", "movie-1") with { Status = "archived", ImportStatus = null, ImportFailureCode = null });
        Listed();

        Assert.Equal(1, await SettlerWithProcessors(processors).SettleAsync(CancellationToken.None));

        processors.Verify(repo => repo.UpdateProcessorHandoffAsync("h-1", "dismissed", null, null, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains(_said, line => line.Category == "processing.source-gone.settled" && line.Message.Contains("Deluno had already closed that download"));
    }

    [Fact]
    public async Task A_gone_download_row_stays_while_nothing_explains_it()
    {
        var processors = WithSourceGone();
        _dispatches.Setup(repo => repo.FindAnyDispatchByHashAsync("client-1", "hash", It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailedImport("d-9", "movie", "movie-1") with { Status = "completed", ImportStatus = null, ImportFailureCode = null });
        Listed();

        Assert.Equal(0, await SettlerWithProcessors(processors).SettleAsync(CancellationToken.None));

        processors.Verify(repo => repo.UpdateProcessorHandoffAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_failed_hand_off_whose_film_holds_its_file_since_is_dismissed_with_a_line()
    {
        var processors = new Mock<Deluno.Platform.Data.IProcessorRepository>();
        processors.Setup(repo => repo.ListProcessorHandoffsWithStatusAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Handoff("failed", "It could not process 1 file.")]);
        _dispatches.Setup(repo => repo.FindAnyDispatchByHashAsync("client-1", "hash", It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailedImport("d-9", "movie", "movie-1"));
        _movies.Setup(repo => repo.GetTitleFileHoldsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Holds("movie-1", Created.AddHours(2)));
        Listed();

        var settler = new HandledProblemsSettler(_movies.Object, _series.Object, _jobs.Object, _dispatches.Object, _activity.Object, processors.Object);
        Assert.Equal(1, await settler.SettleAsync(CancellationToken.None));

        processors.Verify(repo => repo.UpdateProcessorHandoffAsync("h-1", "dismissed", null, null, "It could not process 1 file.", It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains(_said, line => line.Category == "processing.dismissed" && line.Message.Contains("in the library now"));
    }

    [Fact]
    public async Task A_failed_hand_off_stays_when_its_film_has_no_newer_file()
    {
        var processors = new Mock<Deluno.Platform.Data.IProcessorRepository>();
        processors.Setup(repo => repo.ListProcessorHandoffsWithStatusAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Handoff("failed", "It could not process 1 file.")]);
        _dispatches.Setup(repo => repo.FindAnyDispatchByHashAsync("client-1", "hash", It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailedImport("d-9", "movie", "movie-1"));
        Listed();

        var settler = new HandledProblemsSettler(_movies.Object, _series.Object, _jobs.Object, _dispatches.Object, _activity.Object, processors.Object);
        Assert.Equal(0, await settler.SettleAsync(CancellationToken.None));

        processors.Verify(repo => repo.UpdateProcessorHandoffAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Each_case_it_closes_gets_one_line_in_plain_words()
    {
        _movies.Setup(repo => repo.SettleCasesOfTitlesHoldingFilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new SettledRecoveryCase("case-1", "Nosferatu.1922.1080p.BluRay.x264-HD4U [PublicHD]", "importFailed")]);
        _series.Setup(repo => repo.SettleCasesOfTitlesHoldingFilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new SettledRecoveryCase("case-2", "The Twilight Zone S01E02", "downloadFailed")]);
        Listed();

        Assert.Equal(2, await Settler().SettleAsync(CancellationToken.None));

        Assert.Equal(2, _said.Count);
        Assert.All(_said, line => Assert.Equal("recovery.case-settled", line.Category));
        Assert.Contains("is in the library now", _said[0].Message);
    }

    [Fact]
    public async Task A_failed_import_of_a_download_whose_film_holds_its_file_since_is_closed_once_with_a_line()
    {
        Listed(FailedImport("d-1", "movie", "movie-1"));
        _movies.Setup(repo => repo.GetTitleFileHoldsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Holds("movie-1", Created.AddHours(20)));

        Assert.Equal(1, await Settler().SettleAsync(CancellationToken.None));

        _dispatches.Verify(repo => repo.RecordImportOutcomeAsync("d-1", "imported", It.IsAny<string?>(), null, null, It.IsAny<CancellationToken>(), null), Times.Once);
        var line = Assert.Single(_said);
        Assert.Equal("download.import-failure-settled", line.Category);
        Assert.Contains("in the library now", line.Message);
    }

    [Fact]
    public async Task A_relinked_episode_whose_arrival_was_never_dated_closes_its_failure_too()
    {
        Listed(FailedImport("d-2", "episode", "episode-1"));
        _series.Setup(repo => repo.GetTitleFileHoldsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Holds("episode-1", null));

        Assert.Equal(1, await Settler().SettleAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_file_that_was_there_before_the_failure_does_not_hide_it()
    {
        Listed(FailedImport("d-3", "movie", "movie-1"));
        _movies.Setup(repo => repo.GetTitleFileHoldsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Holds("movie-1", Created.AddDays(-30)));

        Assert.Equal(0, await Settler().SettleAsync(CancellationToken.None));

        _dispatches.Verify(repo => repo.RecordImportOutcomeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<IntegrationFailure?>()), Times.Never);
        Assert.Empty(_said);
    }

    // #1182: Weir left one small extra out, Deluno imported the main video, and the hand-off ended up dismissed
    // with the download "Ready to import" and Weir never told.
    private const string ExtrasNote = "Weir left one small extra file out (Gallery.mkv). The main video was delivered and imported.";

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static JobQueueItem Job(string status)
        => new("job-1", "filesystem.import.execute", "processor", status, null, 1, Created, Created, Created, Created, null, null, null, null, null, null, null, 3, Created, null);

    private static Deluno.Platform.Contracts.ProcessorHandoffItem WithImport(string status, string? note)
        => Handoff(status, note) with { ImportJobId = "job-1", OutputPath = "/ready/Nosferatu.mkv" };

    private Mock<Deluno.Platform.Data.IProcessorRepository> Processors(params Deluno.Platform.Contracts.ProcessorHandoffItem[] handoffs)
    {
        var processors = new Mock<Deluno.Platform.Data.IProcessorRepository>();
        processors.Setup(repo => repo.ListProcessorHandoffsWithStatusAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string> statuses, CancellationToken _) => handoffs.Where(item => statuses.Contains(item.Status)).ToList());
        processors.Setup(repo => repo.FindProcessorConnectionAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Deluno.Platform.Contracts.ProcessorConnectionItem("c-1", "Weir", "weir", "http://localhost:1/", "X-Key", "s", true, "healthy", null, null, Created, Created));
        _jobs.Setup(repo => repo.GetAsync("job-1", It.IsAny<CancellationToken>())).ReturnsAsync(Job("completed"));
        return processors;
    }

    private HandledProblemsSettler SettlerTelling(Mock<Deluno.Platform.Data.IProcessorRepository> processors, Mock<Deluno.Platform.Contracts.IProcessorConnectionService> connections)
        => new(_movies.Object, _series.Object, _jobs.Object, _dispatches.Object, _activity.Object, processors.Object, connections.Object, new FixedClock(Created.AddHours(3)));

    private static Mock<Deluno.Platform.Contracts.IProcessorConnectionService> Connections()
    {
        var connections = new Mock<Deluno.Platform.Contracts.IProcessorConnectionService>();
        connections.Setup(service => service.GetStatusSupportAsync(It.IsAny<Deluno.Platform.Contracts.ProcessorConnectionItem>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Deluno.Platform.Contracts.ProcessorStatusSupport(Deluno.Platform.Contracts.ProcessorAnswer.Answered, true, true, null) { CanBeToldTheOutcome = true });
        connections.Setup(service => service.ReportOutcomeAsync(It.IsAny<Deluno.Platform.Contracts.ProcessorConnectionItem>(), It.IsAny<string>(),
                It.IsAny<Deluno.Platform.Contracts.ProcessorHandoffOutcome>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Deluno.Platform.Contracts.ProcessorOutcomeResult(Deluno.Platform.Contracts.ProcessorAnswer.Answered, true, null));
        return connections;
    }

    [Fact]
    public async Task A_completed_hand_off_with_only_an_extras_note_is_not_dismissed_when_its_import_ran()
    {
        var processors = Processors(WithImport("completed", ExtrasNote));
        _dispatches.Setup(repo => repo.FindAnyDispatchByHashAsync("client-1", "hash", It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailedImport("d-9", "movie", "movie-1"));
        _movies.Setup(repo => repo.GetTitleFileHoldsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Holds("movie-1", Created.AddHours(2)));
        Listed();

        await SettlerTelling(processors, Connections()).SettleAsync(CancellationToken.None);

        processors.Verify(repo => repo.UpdateProcessorHandoffAsync("h-1", "completed", null, null, null, It.IsAny<CancellationToken>()), Times.Once);
        processors.Verify(repo => repo.UpdateProcessorHandoffAsync(It.IsAny<string>(), "dismissed", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_dismissed_hand_off_whose_import_ran_tells_its_processor_the_file_was_imported_once()
    {
        var processors = Processors(WithImport("dismissed", ExtrasNote));
        Listed();
        var connections = Connections();

        await SettlerTelling(processors, connections).SettleAsync(CancellationToken.None);

        processors.Verify(repo => repo.SaveProcessorOutcomeAsync(
            It.Is<Deluno.Platform.Contracts.ProcessorOutcomeRecord>(record => record.HandoffId == "h-1" && record.Outcome == "imported"), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        connections.Verify(service => service.ReportOutcomeAsync(It.IsAny<Deluno.Platform.Contracts.ProcessorConnectionItem>(), "h-1",
            Deluno.Platform.Contracts.ProcessorHandoffOutcome.Imported, "/ready/Nosferatu.mkv", null, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_hand_off_whose_processor_was_already_told_is_not_told_again()
    {
        var processors = Processors(WithImport("dismissed", ExtrasNote));
        processors.Setup(repo => repo.GetProcessorOutcomeAsync("h-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Deluno.Platform.Contracts.ProcessorOutcomeRecord("h-1", "Weir", "lib", "r", "imported", null, null, Created, "delivered", 1, Created, null, true, null, Created));
        Listed();
        var connections = Connections();

        await SettlerTelling(processors, connections).SettleAsync(CancellationToken.None);

        connections.Verify(service => service.ReportOutcomeAsync(It.IsAny<Deluno.Platform.Contracts.ProcessorConnectionItem>(), It.IsAny<string>(),
            It.IsAny<Deluno.Platform.Contracts.ProcessorHandoffOutcome>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_dismissed_hand_off_whose_import_did_not_finish_is_not_reported_imported()
    {
        var processors = Processors(WithImport("dismissed", "It failed."));
        _jobs.Setup(repo => repo.GetAsync("job-1", It.IsAny<CancellationToken>())).ReturnsAsync(Job("failed"));
        Listed();
        var connections = Connections();

        await SettlerTelling(processors, connections).SettleAsync(CancellationToken.None);

        connections.Verify(service => service.ReportOutcomeAsync(It.IsAny<Deluno.Platform.Contracts.ProcessorConnectionItem>(), It.IsAny<string>(),
            Deluno.Platform.Contracts.ProcessorHandoffOutcome.Imported, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // #1220: every way a hand-off closes without an import tells its processor, with the reason it carries.
    [Theory]
    [InlineData("You told Deluno to stop tracking Nosferatu, so Deluno will not import it.")]
    [InlineData("The hand-off was dismissed by a person.")]
    [InlineData("Nosferatu was removed from Deluno, so Deluno stopped waiting.")]
    [InlineData("The download of Nosferatu is gone, so Deluno will not import it.")]
    [InlineData("Nosferatu did not go through the processor, but the title was imported from another download, so Deluno closed this one.")]
    public async Task A_hand_off_closed_without_an_import_tells_its_processor_it_will_not_be_imported_with_its_reason(string reason)
    {
        var processors = Processors(Handoff("dismissed", reason));
        Listed();
        var connections = Connections();

        await SettlerTelling(processors, connections).SettleAsync(CancellationToken.None);

        processors.Verify(repo => repo.SaveProcessorOutcomeAsync(
            It.Is<Deluno.Platform.Contracts.ProcessorOutcomeRecord>(record => record.HandoffId == "h-1" && record.Outcome == "not-imported" && record.Reason == reason), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        connections.Verify(service => service.ReportOutcomeAsync(It.IsAny<Deluno.Platform.Contracts.ProcessorConnectionItem>(), "h-1",
            Deluno.Platform.Contracts.ProcessorHandoffOutcome.NotImported, null, reason, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_hand_off_closed_with_no_reason_is_still_reported_with_a_plain_one()
    {
        var processors = Processors(Handoff("dismissed", null));
        Listed();
        var connections = Connections();

        await SettlerTelling(processors, connections).SettleAsync(CancellationToken.None);

        connections.Verify(service => service.ReportOutcomeAsync(It.IsAny<Deluno.Platform.Contracts.ProcessorConnectionItem>(), "h-1",
            Deluno.Platform.Contracts.ProcessorHandoffOutcome.NotImported, null, "The hand-off was closed, so Deluno will not import this file.", It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_closed_hand_off_is_told_only_once_and_a_processor_that_does_not_ask_is_not_called()
    {
        var processors = Processors(Handoff("dismissed", "Closed."));
        Listed();
        var connections = Connections();
        connections.Setup(service => service.GetStatusSupportAsync(It.IsAny<Deluno.Platform.Contracts.ProcessorConnectionItem>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Deluno.Platform.Contracts.ProcessorStatusSupport(Deluno.Platform.Contracts.ProcessorAnswer.Answered, true, true, null) { CanBeToldTheOutcome = false });

        await SettlerTelling(processors, connections).SettleAsync(CancellationToken.None);

        connections.Verify(service => service.ReportOutcomeAsync(It.IsAny<Deluno.Platform.Contracts.ProcessorConnectionItem>(), It.IsAny<string>(),
            It.IsAny<Deluno.Platform.Contracts.ProcessorHandoffOutcome>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
        processors.Verify(repo => repo.SaveProcessorOutcomeAsync(
            It.Is<Deluno.Platform.Contracts.ProcessorOutcomeRecord>(record => record.Delivery == "given-up"), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task An_old_completed_hand_off_with_no_outcome_record_is_not_reported()
    {
        var processors = Processors(WithImport("completed", null));
        Listed();
        var connections = Connections();

        await SettlerTelling(processors, connections).SettleAsync(CancellationToken.None);

        connections.Verify(service => service.ReportOutcomeAsync(It.IsAny<Deluno.Platform.Contracts.ProcessorConnectionItem>(), It.IsAny<string>(),
            It.IsAny<Deluno.Platform.Contracts.ProcessorHandoffOutcome>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // #1182, rig 9 Oct: the settler ran at 02:56:40, the import job completed at 02:56:41. The hand-off was dismissed
    // one second before the exception that keeps it Completed could apply.
    [Theory]
    [InlineData("queued")]
    [InlineData("running")]
    [InlineData("requeued")]
    public async Task A_hand_off_whose_import_job_is_still_in_flight_waits_and_is_settled_Completed_once_it_finishes(string inFlight)
    {
        var processors = Processors(WithImport("completed", ExtrasNote));
        _dispatches.Setup(repo => repo.FindAnyDispatchByHashAsync("client-1", "hash", It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailedImport("d-9", "movie", "movie-1"));
        _movies.Setup(repo => repo.GetTitleFileHoldsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Holds("movie-1", Created.AddHours(2)));
        Listed();

        // The file has arrived but the job has not written "completed" yet.
        _jobs.Setup(repo => repo.GetAsync("job-1", It.IsAny<CancellationToken>())).ReturnsAsync(Job(inFlight));
        var settler = SettlerTelling(processors, Connections());
        await settler.SettleAsync(CancellationToken.None);

        processors.Verify(repo => repo.UpdateProcessorHandoffAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.DoesNotContain(_said, line => line.Category == "processing.dismissed");

        // A second later the job completes; the next pass keeps the hand-off Completed.
        _jobs.Setup(repo => repo.GetAsync("job-1", It.IsAny<CancellationToken>())).ReturnsAsync(Job("completed"));
        await settler.SettleAsync(CancellationToken.None);

        processors.Verify(repo => repo.UpdateProcessorHandoffAsync("h-1", "completed", null, null, null, It.IsAny<CancellationToken>()), Times.Once);
        processors.Verify(repo => repo.UpdateProcessorHandoffAsync(It.IsAny<string>(), "dismissed", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_hand_off_whose_import_job_failed_is_still_dismissed_once_the_title_has_its_file()
    {
        var processors = Processors(WithImport("completed", ExtrasNote));
        _dispatches.Setup(repo => repo.FindAnyDispatchByHashAsync("client-1", "hash", It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailedImport("d-9", "movie", "movie-1"));
        _movies.Setup(repo => repo.GetTitleFileHoldsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Holds("movie-1", Created.AddHours(2)));
        Listed();
        _jobs.Setup(repo => repo.GetAsync("job-1", It.IsAny<CancellationToken>())).ReturnsAsync(Job("failed"));

        await SettlerTelling(processors, Connections()).SettleAsync(CancellationToken.None);

        processors.Verify(repo => repo.UpdateProcessorHandoffAsync("h-1", "dismissed", null, null, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // #1186: "Search for another copy" sends the same release again, so both sends share a hash. The settler judges the
    // hand-off by the newer, live send (the one it belongs to), found the way the gone-download rule finds it.
    [Fact]
    public async Task A_failed_hand_off_is_judged_by_the_newer_live_send_of_its_release_not_the_older_archived_one()
    {
        var processors = new Mock<Deluno.Platform.Data.IProcessorRepository>();
        processors.Setup(repo => repo.ListProcessorHandoffsWithStatusAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Handoff("failed", "It could not process 1 file.")]);
        var older = FailedImport("d-old", "movie", "movie-old") with { Status = "archived", CreatedUtc = Created.AddMinutes(-30) };
        var newer = FailedImport("d-new", "movie", "movie-new") with { Status = "sent", CreatedUtc = Created };
        _dispatches.Setup(repo => repo.FindAnyDispatchByHashAsync("client-1", "hash", It.IsAny<CancellationToken>())).ReturnsAsync(older);
        _dispatches.Setup(repo => repo.FindDispatchesByClientItemIdsAsync("client-1", It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([older]);
        _dispatches.Setup(repo => repo.QueryDispatchesAsync(It.IsAny<DispatchQueryFilter>(), It.IsAny<DispatchPaginationOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(((IReadOnlyList<DownloadDispatchItem>)[newer, older], (string?)null));
        // Only the newer send's title holds a file since the hand-off; the older send's title does not.
        _movies.Setup(repo => repo.GetTitleFileHoldsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<string> ids, CancellationToken _) =>
                ids.Contains("movie-new") ? Holds("movie-new", Created.AddHours(2)) : new TitleFileHolds(new Dictionary<string, DateTimeOffset?>()));
        Listed();

        Assert.Equal(1, await SettlerWithProcessors(processors).SettleAsync(CancellationToken.None));

        processors.Verify(repo => repo.UpdateProcessorHandoffAsync("h-1", "dismissed", null, null, "It could not process 1 file.", It.IsAny<CancellationToken>()), Times.Once);
    }
}
