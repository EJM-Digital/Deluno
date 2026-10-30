using Deluno.Contracts;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The System row that mirrors an open processor-output import issue goes when the issue closes, and every open screen is told to
/// read health again with a "healthy" push (the realtime area of the coverage register). A row that is not there pushes nothing.
/// </summary>
public sealed class AClosedOutputIssueClearsItsHealthRowAndSaysSoTests
{
    private sealed class Recording : NullRealtimeEventPublisher
    {
        public List<(string Source, string Status, string Message)> Health { get; } = [];

        public override Task PublishHealthChangedAsync(string source, string status, string message, CancellationToken cancellationToken)
        {
            Health.Add((source, status, message));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Closing_the_issue_removes_its_row_and_pushes_HealthChanged_healthy_and_a_second_close_pushes_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var health = app.Services.GetRequiredService<IHealthCheckRepository>();
        var rowId = ProblemTwins.OutputIssueRowId("case-1");
        await health.UpsertResultsAsync(
            [HealthCheckOutcome.Problem(rowId, HealthCheckCategories.Processor, "Processor output", "Example Film could not be imported.", "The file is damaged.", "Send it again.")],
            CancellationToken.None);
        Assert.NotNull(await health.GetAsync(rowId, CancellationToken.None));
        var realtime = new Recording();

        await ProblemTwins.ClearHealthRowAsync("case-1", health, realtime, CancellationToken.None);

        Assert.Null(await health.GetAsync(rowId, CancellationToken.None));
        var push = Assert.Single(realtime.Health);
        Assert.Equal(("system.health", "healthy"), (push.Source, push.Status));

        await ProblemTwins.ClearHealthRowAsync("case-1", health, realtime, CancellationToken.None);
        Assert.Single(realtime.Health);
    }
}
