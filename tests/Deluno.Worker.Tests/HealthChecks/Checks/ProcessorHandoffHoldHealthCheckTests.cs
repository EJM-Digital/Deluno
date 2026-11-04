using Deluno.Contracts;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Worker.HealthChecks.Checks;
using Moq;

namespace Deluno.Worker.Tests.HealthChecks.Checks;

/// <summary>
/// #914, #929: a hand-off Deluno stopped waiting on is held for a person to
/// decide -- it must show on System &#8594; Health, not only in Queue's own
/// list, so it also lights the Dashboard tile and the bell.
/// </summary>
public sealed class ProcessorHandoffHoldHealthCheckTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-20T12:00:00Z");

    private static ProcessorHandoffItem Handoff(string status, string? failureMessage) => new(
        "handoff-1", "library-1", "movie", "client-1", "queue-1", "Sintel.2010.1080p", "/downloads/sintel",
        "Weir", status, null, null, failureMessage, Now.AddDays(-1), Now.AddHours(-3));

    [Fact]
    public async Task A_stalled_hand_off_is_reported_as_a_problem_with_its_own_reason_and_options()
    {
        var repository = new Mock<IProcessorRepository>();
        repository
            .Setup(repo => repo.ListProcessorHandoffsWithStatusAsync(
                It.Is<IReadOnlyList<string>>(statuses => statuses.Contains(ProcessorHandoffStatuses.Stalled)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([Handoff(ProcessorHandoffStatuses.Stalled, "Weir reported no change for 2 days.")]);
        var check = new ProcessorHandoffHoldHealthCheck(repository.Object);

        var outcome = Assert.Single(await check.RunAsync(CancellationToken.None));

        Assert.Equal(HealthCheckCategories.Processor, check.Category);
        Assert.Equal(HealthCheckStatuses.Problem, outcome.Status);
        Assert.Equal("Weir reported no change for 2 days.", outcome.Message);
        Assert.Contains("Import the original now", outcome.Fix);
        Assert.Contains("keep waiting", outcome.Fix);
        Assert.Contains("ask the processor again", outcome.Fix);
        Assert.Contains("give up and search", outcome.Fix);
    }

    [Fact]
    public async Task A_lost_hand_off_offers_the_three_options_specific_to_it()
    {
        var repository = new Mock<IProcessorRepository>();
        repository
            .Setup(repo => repo.ListProcessorHandoffsWithStatusAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Handoff(ProcessorHandoffStatuses.Lost, "Weir says it never received Sintel.2010.1080p (asked 10:00 and 12:00).")]);
        var check = new ProcessorHandoffHoldHealthCheck(repository.Object);

        var outcome = Assert.Single(await check.RunAsync(CancellationToken.None));

        Assert.Equal("Import the original, hand it to the processor again, or search for another release.", outcome.Fix);
    }

    [Fact]
    public async Task Nothing_is_reported_when_no_hand_off_is_held()
    {
        var repository = new Mock<IProcessorRepository>();
        repository
            .Setup(repo => repo.ListProcessorHandoffsWithStatusAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var check = new ProcessorHandoffHoldHealthCheck(repository.Object);

        Assert.Empty(await check.RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_hand_off_with_no_reason_and_no_processor_name_says_so_in_plain_words_and_names_its_details()
    {
        var repository = new Mock<IProcessorRepository>();
        repository
            .Setup(repo => repo.ListProcessorHandoffsWithStatusAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Handoff(ProcessorHandoffStatuses.TimedOut, null) with { ProcessorName = null }]);
        var check = new ProcessorHandoffHoldHealthCheck(repository.Object);

        var outcome = Assert.Single(await check.RunAsync(CancellationToken.None));

        Assert.Equal("processor-handoff:handoff-1", outcome.Id);
        Assert.Equal("Sintel.2010.1080p is held with its processor", outcome.Name);
        Assert.Equal("Sintel.2010.1080p is being held rather than handed to import.", outcome.Message);
        Assert.Equal("Import the original now, keep waiting, ask the processor again, or give up and search for another release.", outcome.Fix);
        Assert.Contains("\"handoffId\":\"handoff-1\"", outcome.DetailsJson);
        Assert.Contains("\"libraryId\":\"library-1\"", outcome.DetailsJson);
        Assert.Contains("\"status\":\"" + ProcessorHandoffStatuses.TimedOut + "\"", outcome.DetailsJson);
    }

    [Fact]
    public async Task An_empty_reason_is_not_shown_and_a_named_processor_is_in_the_title()
    {
        var repository = new Mock<IProcessorRepository>();
        repository
            .Setup(repo => repo.ListProcessorHandoffsWithStatusAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Handoff(ProcessorHandoffStatuses.Stalled, string.Empty)]);
        var check = new ProcessorHandoffHoldHealthCheck(repository.Object);

        var outcome = Assert.Single(await check.RunAsync(CancellationToken.None));

        Assert.Equal("Sintel.2010.1080p is held with Weir", outcome.Name);
        Assert.Equal("Sintel.2010.1080p is being held rather than handed to import.", outcome.Message);
    }

    [Fact]
    public async Task It_looks_for_exactly_the_stalled_timed_out_and_lost_hand_offs()
    {
        IReadOnlyList<string>? asked = null;
        var repository = new Mock<IProcessorRepository>();
        repository
            .Setup(repo => repo.ListProcessorHandoffsWithStatusAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback((IReadOnlyList<string> statuses, CancellationToken _) => asked = statuses)
            .ReturnsAsync([]);

        await new ProcessorHandoffHoldHealthCheck(repository.Object).RunAsync(CancellationToken.None);

        Assert.Equal([ProcessorHandoffStatuses.Stalled, ProcessorHandoffStatuses.TimedOut, ProcessorHandoffStatuses.Lost], asked);
    }
}
