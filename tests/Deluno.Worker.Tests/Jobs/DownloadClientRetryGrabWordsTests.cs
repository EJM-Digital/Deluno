using System.Text.Json;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Worker.Jobs;
using Deluno.Worker.Tests.Support;
using Moq;

namespace Deluno.Worker.Tests.Jobs;

/// <summary>#875: every sentence a retry says, to the person and in Activity, and what it writes on the download's record.</summary>
public sealed class DownloadClientRetryGrabWordsTests
{
    private const string Release = "Tears.of.Steel.2012";

    private static string Payload(int attempt) => JsonSerializer.Serialize(new
    {
        DownloadClientId = "client-1", DownloadClientName = "SABnzbd", LibraryId = "library-1", MediaType = "movies", EntityType = "movie",
        EntityId = "movie-1", Title = "Tears of Steel", ReleaseName = Release, DownloadUrl = "http://fixture.invalid/x.nzb",
        Category = "movies", IndexerName = "Fixture", AttemptNumber = attempt
    });

    private static (string Message, List<(string Kind, string Text)> Activity, Mock<IJobQueueRepository> Jobs) Run(DownloadClientGrabResult result, int attempt)
    {
        var activity = new List<(string, string)>();
        var feed = new Mock<IActivityFeedRepository>();
        feed.Setup(a => a.RecordActivityAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string?, string?, string?, string?, CancellationToken>((kind, text, _, _, _, _, _) => activity.Add((kind, text)))
            .ReturnsAsync((ActivityEventItem)null!);
        var grabs = new Mock<IDownloadClientGrabService>();
        grabs.Setup(g => g.GrabAsync(It.IsAny<string>(), It.IsAny<DownloadClientGrabRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(result);
        var scheduler = new Mock<IJobScheduler>();
        scheduler.Setup(s => s.EnqueueAsync(It.IsAny<EnqueueJobRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((EnqueueJobRequest r, CancellationToken _) => TestJobs.Create(r.JobType, r.PayloadJson));
        var jobs = new Mock<IJobQueueRepository>();
        var handler = new DownloadClientRetryGrabJobHandler(grabs.Object, jobs.Object, feed.Object, scheduler.Object, TimeProvider.System);
        var message = handler.HandleAsync(TestJobs.Create(SearchExecutionSupport.DownloadClientRetryGrabJobType, payloadJson: Payload(attempt)), CancellationToken.None).GetAwaiter().GetResult();
        return (message, activity, jobs);
    }

    private static DownloadClientGrabResult Failed(IntegrationFailureKind kind, string? failureCode = null)
        => new("client-1", Release, false, "failed", "SABnzbd could not be reached.")
        {
            FailureCode = failureCode,
            Failure = IntegrationFailureFactory.FromLegacy("download-client", "client-1", "SABnzbd", "grab", null, "SABnzbd could not be reached.") with { Kind = kind }
        };

    [Fact]
    public void A_retry_that_works_says_so_to_the_person_and_in_Activity_and_records_a_good_answer()
    {
        var (message, activity, jobs) = Run(new DownloadClientGrabResult("client-1", Release, true, "sent", "Sent.") { ExternalId = "nzo-1" }, 1);

        Assert.Equal($"{Release} was sent to SABnzbd on retry.", message);
        Assert.Equal(("download.client.connectivity-retry-succeeded", $"{Release} was sent to SABnzbd on retry. The earlier setup problem has cleared."), Assert.Single(activity));
        var recorded = Assert.Single(jobs.Invocations, call => call.Method.Name == nameof(IJobQueueRepository.RecordDownloadDispatchAsync));
        Assert.Contains(200, recorded.Arguments);
        Assert.Contains("Fixture", recorded.Arguments);
    }

    [Fact]
    public void An_answer_that_is_not_a_setup_problem_is_left_to_the_dead_release_handling()
    {
        var (message, activity, jobs) = Run(Failed(IntegrationFailureKind.RejectedAction, "dead-release"), 1);

        Assert.Equal($"{Release} was not resent to SABnzbd: SABnzbd could not be reached.", message);
        Assert.Empty(activity);
        var recorded = Assert.Single(jobs.Invocations, call => call.Method.Name == nameof(IJobQueueRepository.RecordDownloadDispatchAsync));
        Assert.Contains(400, recorded.Arguments);
    }

    [Fact]
    public void A_setup_problem_on_the_last_try_gives_up_and_says_how_many_tries_it_took()
    {
        var (message, activity, _) = Run(Failed(IntegrationFailureKind.Unavailable), SearchExecutionSupport.MaxConnectivityRetryAttempts);

        Assert.Equal($"{Release} still could not be sent to SABnzbd; giving up dedicated retries.", message);
        var line = Assert.Single(activity);
        Assert.Equal("download.client.connectivity-retry-exhausted", line.Kind);
        Assert.Equal($"{Release} still could not be sent to SABnzbd after {SearchExecutionSupport.MaxConnectivityRetryAttempts} tries: SABnzbd could not be reached. Deluno has stopped retrying this on its own; the next scheduled search will try again.", line.Text);
    }

    [Fact]
    public void A_setup_problem_before_the_last_try_schedules_another_and_says_it_is_not_the_release()
    {
        var (message, activity, _) = Run(Failed(IntegrationFailureKind.Unavailable), 2);

        Assert.Equal($"{Release} still could not be sent to SABnzbd; another retry is scheduled.", message);
        var line = Assert.Single(activity);
        Assert.Equal("download.client.connectivity-retry-scheduled", line.Kind);
        Assert.Equal($"{Release} still could not be sent to SABnzbd: SABnzbd could not be reached. This is a setup problem, not the release — Deluno will try again shortly.", line.Text);
    }

    [Fact]
    public void The_failure_code_written_on_the_record_is_the_typed_ones_and_otherwise_the_plain_one()
    {
        var withCode = Failed(IntegrationFailureKind.RejectedAction, "plain-code");
        var typed = Run(withCode with { Failure = withCode.Failure! with { Code = "typed-code" } }, 1);
        var plainOnly = Run(new DownloadClientGrabResult("client-1", Release, false, "failed", "No.") { FailureCode = "plain-code" }, 1);

        var typedCode = Assert.Single(typed.Jobs.Invocations, c => c.Method.Name == nameof(IJobQueueRepository.RecordDownloadDispatchAsync)).Arguments.OfType<string>().ToArray();
        var plainCode = Assert.Single(plainOnly.Jobs.Invocations, c => c.Method.Name == nameof(IJobQueueRepository.RecordDownloadDispatchAsync)).Arguments.OfType<string>().ToArray();
        Assert.Contains("typed-code", typedCode);
        Assert.DoesNotContain("plain-code", typedCode);
        Assert.Contains("plain-code", plainCode);
    }
}
