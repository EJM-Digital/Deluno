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
        repository.Setup(repo => repo.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<HealthCheckResultItem>());
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
    public async Task An_advisory_tier_problem_is_announced_with_the_advisory_suffix_but_recorded_plainly_in_activity()
    {
        var repository = new Mock<IHealthCheckRepository>();
        repository.Setup(repo => repo.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<HealthCheckResultItem>());
        repository.Setup(repo => repo.UpsertResultsAsync(It.IsAny<IReadOnlyList<HealthCheckOutcome>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<HealthCheckOutcome> outcomes, CancellationToken _) => outcomes
                .Select(outcome => new HealthCheckTransitionResult(
                    new HealthCheckResultItem(outcome.Id, outcome.Category, outcome.Name, outcome.Status, outcome.Message, outcome.Cause, outcome.Fix, outcome.DetailsJson, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null),
                    HealthCheckTransition.Started))
                .ToArray());

        var activity = new Mock<IActivityFeedRepository>();
        var notifications = new Mock<IOutboundNotificationService>();
        var realtime = new Mock<IRealtimeEventPublisher>();
        var check = FakeCheck(HealthCheckCategories.Metadata, [HealthCheckOutcome.Problem("metadata:1", HealthCheckCategories.Metadata, "Metadata", "Slow.", "cause", "fix")]);

        var runner = new HealthCheckRunner([check], repository.Object, activity.Object, notifications.Object, realtime.Object, NullLogger<HealthCheckRunner>.Instance);
        await runner.RunAllAsync(CancellationToken.None);

        activity.Verify(feed => feed.RecordActivityAsync(
            "health.problem", It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), "health-check", "metadata:1", It.IsAny<CancellationToken>()),
            Times.Once);
        notifications.Verify(service => service.DispatchAsync(
            "health.problem" + HealthCheckCategories.OutboundWarningSuffix, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task An_unchanged_result_is_stored_but_not_announced_again()
    {
        var repository = new Mock<IHealthCheckRepository>();
        repository.Setup(repo => repo.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<HealthCheckResultItem>());
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
        repository.Setup(repo => repo.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<HealthCheckResultItem>());
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
        repository.Setup(repo => repo.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<HealthCheckResultItem>());
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

    /// <summary>
    /// A check that reports one row per item it currently finds -- a held
    /// processor hand-off, an unexpected file -- stops mentioning an id the
    /// moment the item is gone. Nothing removed the old row otherwise, so it
    /// stayed a "problem" on Health for ever (#914, #921, #930).
    /// </summary>
    [Fact]
    public async Task A_row_no_check_in_this_pass_still_mentions_is_removed()
    {
        var repository = new Mock<IHealthCheckRepository>();
        repository.Setup(repo => repo.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new HealthCheckResultItem("processor-handoff:1", HealthCheckCategories.Processor, "Resolved hand-off", HealthCheckStatuses.Problem, "Held.", null, null, null, DateTimeOffset.UtcNow.AddMinutes(-15), DateTimeOffset.UtcNow.AddMinutes(-15), null),
            new HealthCheckResultItem("download-client:1", HealthCheckCategories.DownloadClient, "Other category", HealthCheckStatuses.Problem, "Down.", null, null, null, DateTimeOffset.UtcNow.AddMinutes(-15), DateTimeOffset.UtcNow.AddMinutes(-15), null)
        ]);
        repository.Setup(repo => repo.UpsertResultsAsync(It.IsAny<IReadOnlyList<HealthCheckOutcome>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<HealthCheckOutcome> outcomes, CancellationToken _) => outcomes
                .Select(outcome => new HealthCheckTransitionResult(
                    new HealthCheckResultItem(outcome.Id, outcome.Category, outcome.Name, outcome.Status, outcome.Message, outcome.Cause, outcome.Fix, outcome.DetailsJson, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null),
                    HealthCheckTransition.Started))
                .ToArray());

        // This pass's processor check no longer reports "processor-handoff:1" --
        // it was resolved -- but it does still report a different id.
        var check = FakeCheck(HealthCheckCategories.Processor, [HealthCheckOutcome.Problem("processor-handoff:2", HealthCheckCategories.Processor, "Still held", "Held.", "cause", "fix")]);

        var runner = new HealthCheckRunner([check], repository.Object, Mock.Of<IActivityFeedRepository>(), Mock.Of<IOutboundNotificationService>(), Mock.Of<IRealtimeEventPublisher>(), NullLogger<HealthCheckRunner>.Instance);
        await runner.RunAllAsync(CancellationToken.None);

        repository.Verify(repo => repo.RemoveAsync("processor-handoff:1", It.IsAny<CancellationToken>()), Times.Once);
        // A row in a category nothing this pass ran is left alone.
        repository.Verify(repo => repo.RemoveAsync("download-client:1", It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// A "did not answer" row is Unknown, not a problem, so it turning Ok started and cleared nothing and used to push
    /// nothing: the open System page kept it until something else changed (#1229).
    /// </summary>
    [Fact]
    public async Task An_unknown_row_turning_fine_pushes_a_health_change_to_open_screens()
    {
        var repository = new Mock<IHealthCheckRepository>();
        repository.Setup(repo => repo.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<HealthCheckResultItem>());
        repository.Setup(repo => repo.UpsertResultsAsync(It.IsAny<IReadOnlyList<HealthCheckOutcome>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<HealthCheckOutcome> outcomes, CancellationToken _) => outcomes
                .Select(outcome => new HealthCheckTransitionResult(
                    new HealthCheckResultItem(outcome.Id, outcome.Category, outcome.Name, outcome.Status, outcome.Message, outcome.Cause, outcome.Fix, outcome.DetailsJson, DateTimeOffset.UtcNow, null, null),
                    HealthCheckTransition.Unchanged,
                    ScreenChanged: true))
                .ToArray());
        var realtime = new Mock<IRealtimeEventPublisher>();
        var check = FakeCheck(HealthCheckCategories.Processor, [HealthCheckOutcome.Ok("processor:1:watching:2", HealthCheckCategories.Processor, "Watches", "Fine.")]);

        var runner = new HealthCheckRunner([check], repository.Object, Mock.Of<IActivityFeedRepository>(), Mock.Of<IOutboundNotificationService>(), realtime.Object, NullLogger<HealthCheckRunner>.Instance);
        await runner.RunAllAsync(CancellationToken.None);

        realtime.Verify(publisher => publisher.PublishHealthChangedAsync("system.health", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_row_that_is_only_removed_pushes_a_health_change_and_an_untouched_pass_pushes_none()
    {
        var repository = new Mock<IHealthCheckRepository>();
        repository.Setup(repo => repo.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new HealthCheckResultItem("processor:1:watching:2", HealthCheckCategories.Processor, "Gone", HealthCheckStatuses.Unknown, "No answer.", null, null, null, DateTimeOffset.UtcNow.AddMinutes(-5), null, null)
        ]);
        var realtime = new Mock<IRealtimeEventPublisher>();
        var check = FakeCheck(HealthCheckCategories.Processor, []);

        var runner = new HealthCheckRunner([check], repository.Object, Mock.Of<IActivityFeedRepository>(), Mock.Of<IOutboundNotificationService>(), realtime.Object, NullLogger<HealthCheckRunner>.Instance);
        await runner.RunAllAsync(CancellationToken.None);

        repository.Verify(repo => repo.RemoveAsync("processor:1:watching:2", It.IsAny<CancellationToken>()), Times.Once);
        realtime.Verify(publisher => publisher.PublishHealthChangedAsync("system.health", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);

        var quietRepository = new Mock<IHealthCheckRepository>();
        quietRepository.Setup(repo => repo.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<HealthCheckResultItem>());
        quietRepository.Setup(repo => repo.UpsertResultsAsync(It.IsAny<IReadOnlyList<HealthCheckOutcome>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<HealthCheckOutcome> outcomes, CancellationToken _) => outcomes
                .Select(outcome => new HealthCheckTransitionResult(
                    new HealthCheckResultItem(outcome.Id, outcome.Category, outcome.Name, outcome.Status, outcome.Message, outcome.Cause, outcome.Fix, outcome.DetailsJson, DateTimeOffset.UtcNow, null, null),
                    HealthCheckTransition.Unchanged))
                .ToArray());
        var quietRealtime = new Mock<IRealtimeEventPublisher>();
        var quietRunner = new HealthCheckRunner([FakeCheck(HealthCheckCategories.Processor, [HealthCheckOutcome.Ok("a", HealthCheckCategories.Processor, "A", "Fine.")])], quietRepository.Object, Mock.Of<IActivityFeedRepository>(), Mock.Of<IOutboundNotificationService>(), quietRealtime.Object, NullLogger<HealthCheckRunner>.Instance);
        await quietRunner.RunAllAsync(CancellationToken.None);
        quietRealtime.Verify(publisher => publisher.PublishHealthChangedAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static Deluno.Worker.HealthChecks.IHealthCheck FakeCheck(string category, IReadOnlyList<HealthCheckOutcome> outcomes)
    {
        var check = new Mock<Deluno.Worker.HealthChecks.IHealthCheck>();
        check.SetupGet(instance => instance.Category).Returns(category);
        check.Setup(instance => instance.RunAsync(It.IsAny<CancellationToken>())).ReturnsAsync(outcomes);
        return check.Object;
    }
}
