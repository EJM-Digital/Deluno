using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Notifications;
using Deluno.Worker.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Deluno.Worker.Tests.HealthChecks;

/// <summary>
/// The runner's whole job is deciding what is worth telling somebody about
/// (#909): every check's result is always written to the shared board, but
/// only a check starting or clearing is announced -- and Library, which
/// already announces itself the moment it changes
/// (<c>LibraryAvailabilityService</c>), is never announced twice.
/// </summary>
public sealed class HealthCheckRunnerTests
{
    [Fact]
    public async Task A_check_that_starts_failing_is_recorded_and_announced_once()
    {
        var repository = new Mock<IHealthCheckRepository>();
        repository.Setup(repo => repo.UpsertResultsAsync(It.IsAny<IReadOnlyList<HealthCheckOutcome>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<HealthCheckOutcome> outcomes, CancellationToken _) => outcomes
                .Select(outcome => new HealthCheckTransitionResult(
                    new HealthCheckResultItem(outcome.Id, outcome.Category, outcome.Name, outcome.Status, outcome.Message, outcome.Cause, outcome.Fix, outcome.DetailsJson, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null),
                    HealthCheckTransition.Started))
                .ToArray());

        var activity = new Mock<IActivityFeedRepository>();
        var notifications = new Mock<IOutboundNotificationService>();
        var realtime = new Mock<IRealtimeEventPublisher>();
        var check = FakeCheck(HealthCheckCategories.DownloadClient, [HealthCheckOutcome.Problem("download-client:1", HealthCheckCategories.DownloadClient, "Client", "Down.", "cause", "fix")]);

        var runner = new HealthCheckRunner([check], repository.Object, activity.Object, notifications.Object, realtime.Object, NullLogger<HealthCheckRunner>.Instance);
        await runner.RunAllAsync(CancellationToken.None);

        activity.Verify(feed => feed.RecordActivityAsync(
            "health.problem", It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), "health-check", "download-client:1", It.IsAny<CancellationToken>()),
            Times.Once);
        notifications.Verify(service => service.DispatchAsync(
            "health.problem", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task An_unchanged_result_is_stored_but_not_announced_again()
    {
        var repository = new Mock<IHealthCheckRepository>();
        repository.Setup(repo => repo.UpsertResultsAsync(It.IsAny<IReadOnlyList<HealthCheckOutcome>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<HealthCheckOutcome> outcomes, CancellationToken _) => outcomes
                .Select(outcome => new HealthCheckTransitionResult(
                    new HealthCheckResultItem(outcome.Id, outcome.Category, outcome.Name, outcome.Status, outcome.Message, outcome.Cause, outcome.Fix, outcome.DetailsJson, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(-2), null),
                    HealthCheckTransition.Unchanged))
                .ToArray());

        var activity = new Mock<IActivityFeedRepository>();
        var notifications = new Mock<IOutboundNotificationService>();
        var realtime = new Mock<IRealtimeEventPublisher>();
        var check = FakeCheck(HealthCheckCategories.DownloadClient, [HealthCheckOutcome.Problem("download-client:1", HealthCheckCategories.DownloadClient, "Client", "Still down.", "cause", "fix")]);

        var runner = new HealthCheckRunner([check], repository.Object, activity.Object, notifications.Object, realtime.Object, NullLogger<HealthCheckRunner>.Instance);
        await runner.RunAllAsync(CancellationToken.None);

        activity.VerifyNoOtherCalls();
        notifications.VerifyNoOtherCalls();
        realtime.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_library_problem_is_not_announced_a_second_time()
    {
        var repository = new Mock<IHealthCheckRepository>();
        repository.Setup(repo => repo.UpsertResultsAsync(It.IsAny<IReadOnlyList<HealthCheckOutcome>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<HealthCheckOutcome> outcomes, CancellationToken _) => outcomes
                .Select(outcome => new HealthCheckTransitionResult(
                    new HealthCheckResultItem(outcome.Id, outcome.Category, outcome.Name, outcome.Status, outcome.Message, outcome.Cause, outcome.Fix, outcome.DetailsJson, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null),
                    HealthCheckTransition.Started))
                .ToArray());

        var activity = new Mock<IActivityFeedRepository>();
        var notifications = new Mock<IOutboundNotificationService>();
        var realtime = new Mock<IRealtimeEventPublisher>();
        // Library already announces pause/resume itself the moment it changes
        // (LibraryAvailabilityService); the runner must stay quiet for it.
        var check = FakeCheck(HealthCheckCategories.Library, [HealthCheckOutcome.Problem("library:1:root", HealthCheckCategories.Library, "Library", "Paused.", "cause", "fix")]);

        var runner = new HealthCheckRunner([check], repository.Object, activity.Object, notifications.Object, realtime.Object, NullLogger<HealthCheckRunner>.Instance);
        await runner.RunAllAsync(CancellationToken.None);

        activity.VerifyNoOtherCalls();
        notifications.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Running_only_the_categories_a_setting_affects_skips_the_rest()
    {
        var repository = new Mock<IHealthCheckRepository>();
        var seenCategories = new List<string>();
        repository.Setup(repo => repo.UpsertResultsAsync(It.IsAny<IReadOnlyList<HealthCheckOutcome>>(), It.IsAny<CancellationToken>()))
            .Callback((IReadOnlyList<HealthCheckOutcome> outcomes, CancellationToken _) => seenCategories.AddRange(outcomes.Select(o => o.Category)))
            .ReturnsAsync((IReadOnlyList<HealthCheckOutcome> outcomes, CancellationToken _) => outcomes
                .Select(outcome => new HealthCheckTransitionResult(
                    new HealthCheckResultItem(outcome.Id, outcome.Category, outcome.Name, outcome.Status, outcome.Message, outcome.Cause, outcome.Fix, outcome.DetailsJson, DateTimeOffset.UtcNow, null, null),
                    HealthCheckTransition.Unchanged))
                .ToArray());

        var indexerCheck = FakeCheck(HealthCheckCategories.Indexer, [HealthCheckOutcome.Ok("indexer:1", HealthCheckCategories.Indexer, "Indexer", "Fine.")]);
        var clientCheck = FakeCheck(HealthCheckCategories.DownloadClient, [HealthCheckOutcome.Ok("download-client:1", HealthCheckCategories.DownloadClient, "Client", "Fine.")]);

        var runner = new HealthCheckRunner(
            [indexerCheck, clientCheck],
            repository.Object,
            Mock.Of<IActivityFeedRepository>(),
            Mock.Of<IOutboundNotificationService>(),
            Mock.Of<IRealtimeEventPublisher>(),
            NullLogger<HealthCheckRunner>.Instance);

        await runner.RunCategoriesAsync([HealthCheckCategories.Indexer], CancellationToken.None);

        Assert.Contains(HealthCheckCategories.Indexer, seenCategories);
        Assert.DoesNotContain(HealthCheckCategories.DownloadClient, seenCategories);
    }

    private static Deluno.Worker.HealthChecks.IHealthCheck FakeCheck(string category, IReadOnlyList<HealthCheckOutcome> outcomes)
    {
        var check = new Mock<Deluno.Worker.HealthChecks.IHealthCheck>();
        check.SetupGet(instance => instance.Category).Returns(category);
        check.Setup(instance => instance.RunAsync(It.IsAny<CancellationToken>())).ReturnsAsync(outcomes);
        return check.Object;
    }
}
