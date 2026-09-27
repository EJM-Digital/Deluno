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
}
