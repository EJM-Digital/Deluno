using Deluno.Contracts;
using Deluno.Integrations.Subtitles;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Media;
using Deluno.Worker.Jobs;
using Deluno.Worker.Tests.Support;
using Moq;

namespace Deluno.Worker.Tests.Jobs;

/// <summary>
/// A payload that cannot be read, a rejected subtitle, a provider that is down or has no quota, no provider at all and
/// a cancelled job: each is recorded or refused in words, and a rejected subtitle is never offered again.
/// </summary>
public sealed class TheSubtitleSearchSurvivesAFailingProviderTests
{
    private const string Payload = "{\"libraryId\":\"library-1\"}";

    private static LibraryItem Library() => new(
        Id: "library-1", Name: "Films", MediaType: "movies", Purpose: "collection", RootPath: "/films", DownloadsPath: null,
        QualityProfileId: null, QualityProfileName: null, CutoffQuality: null, UpgradeUntilCutoff: true, UpgradeUnknownItems: true,
        ImportWorkflow: "standard", ProcessorName: null, ProcessorOutputPath: null, ProcessorTimeoutMinutes: 60, ProcessorFailureMode: "hold",
        AutoSearchEnabled: false, MissingSearchEnabled: false, UpgradeSearchEnabled: false, SearchIntervalMinutes: 6, RetryDelayHours: 12,
        MaxItemsPerRun: 50, SearchWindowStartHour: null, SearchWindowEndHour: null, AutomationStatus: "active", SearchRequested: false,
        LastSearchedUtc: null, NextSearchUtc: null, CreatedUtc: DateTimeOffset.UnixEpoch, UpdatedUtc: DateTimeOffset.UnixEpoch)
    { SubtitleLanguages = ["en"] };

    private sealed class Harness
    {
        public Mock<IMediaSubtitleRepository> Subtitles { get; } = new();
        public Mock<IActivityFeedRepository> Activity { get; } = new();
        public Mock<IJobScheduler> Scheduler { get; } = new();
        public Mock<ILibrariesRepository> Libraries { get; } = new();
        public List<string?> Attempts { get; } = [];
        public List<string> Activities { get; } = [];
        public List<(string Key, string Reason)> Blocked { get; } = [];
        public Mock<ISubtitleFetchService> Fetch { get; } = new();
        public int Calls { get; private set; }

        public Harness(SubtitleFetchOutcome outcome)
        {
            Fetch.Setup(f => f.FetchAsync(
                    It.IsAny<SubtitleSearchRequest>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<CancellationToken>(), It.IsAny<SubtitleContentModificationPolicy?>(), It.IsAny<SubtitleNamePolicy?>(),
                    It.IsAny<bool>(), It.IsAny<IReadOnlyCollection<string>?>()))
                .Callback(() => Calls++)
                .ReturnsAsync(outcome);
            Fetch.Setup(f => f.FetchForcedAsync(
                    It.IsAny<SubtitleSearchRequest>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>(),
                    It.IsAny<CancellationToken>(), It.IsAny<SubtitleContentModificationPolicy?>(), It.IsAny<SubtitleNamePolicy?>(),
                    It.IsAny<bool>(), It.IsAny<IReadOnlyCollection<string>?>()))
                .ReturnsAsync(outcome);
            Libraries.Setup(l => l.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([Library()]);
            Subtitles.Setup(s => s.ListWantedAsync(
                    It.IsAny<MediaKind>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([new MediaSubtitleWantedItem("movie-1", "/films/One.mkv", "One", 2020, null, null, null, "One.2020.WEB", ["en"])]);
            Subtitles.Setup(s => s.ListSubtitlesAsync(It.IsAny<MediaKind>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
            Subtitles.Setup(s => s.ListBlockedKeysAsync(It.IsAny<MediaKind>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new HashSet<string>());
            Subtitles.Setup(s => s.RecordAttemptAsync(
                    It.IsAny<MediaKind>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<IntegrationFailure?>()))
                .Callback<MediaKind, string, string, string?, TimeSpan, CancellationToken, IntegrationFailure?>((_, _, _, result, _, _, _) => Attempts.Add(result))
                .Returns(Task.CompletedTask);
            Subtitles.Setup(s => s.BlockSubtitleAsync(
                    It.IsAny<MediaKind>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback<MediaKind, string, string, string, string?, string?, string, CancellationToken>((_, _, _, key, _, _, reason, _) => Blocked.Add((key, reason)))
                .Returns(Task.CompletedTask);
            Activity.Setup(a => a.RecordActivityAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .Callback<string, string, string?, string?, string?, string?, CancellationToken>((_, message, _, _, _, _, _) => Activities.Add(message))
                .ReturnsAsync((string category, string message, string? payload, string? jobId, string? type, string? id, CancellationToken _) =>
                    new ActivityEventItem("activity-1", category, message, payload, jobId, type, id, DateTimeOffset.UnixEpoch));
        }

        public Task<string> RunAsync(string? payload = Payload, CancellationToken token = default)
        {
            var handler = new LibrarySubtitleSearchJobHandler(Libraries.Object, Subtitles.Object, Fetch.Object, Scheduler.Object, Activity.Object);
            return handler.HandleAsync(TestJobs.Create("library.subtitles.search", payloadJson: payload), token);
        }
    }

    private static IntegrationFailure Failure(IntegrationFailureKind kind) => new(
        "subtitles", "provider-1", "Provider", "search", kind, IntegrationRetryState.RetryScheduled, "The provider did not answer.");

    private static SubtitleFetchOutcome NotFound(string reason, IntegrationFailure? failure = null, IReadOnlyList<RejectedSubtitleCandidate>? rejected = null)
        => new("en", false, null, null, false, reason, Failures: failure is null ? null : [failure], RejectedCandidates: rejected);

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{}")]
    public async Task A_payload_that_cannot_be_read_fails_the_job_and_asks_no_provider(string payload)
    {
        var harness = new Harness(NotFound("none"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.RunAsync(payload));

        Assert.Equal(0, harness.Calls);
    }

    [Fact]
    public async Task A_rejected_subtitle_is_blocklisted_and_named_in_Activity_so_it_is_never_offered_again()
    {
        var rejected = new RejectedSubtitleCandidate("opensubtitles:42", "en", "opensubtitles", "One.2020.CAM", "It is for a different cut.");
        var harness = new Harness(NotFound("Nothing usable.", rejected: [rejected]));

        await harness.RunAsync();

        var blocked = Assert.Single(harness.Blocked);
        Assert.Equal("opensubtitles:42", blocked.Key);
        Assert.Equal("It is for a different cut.", blocked.Reason);
        var line = Assert.Single(harness.Activities);
        Assert.Contains("Rejected a en subtitle for One from opensubtitles: It is for a different cut.", line);
        Assert.Contains("It will not be fetched again", line);
    }

    [Fact]
    public async Task A_provider_that_is_down_is_recorded_as_a_try_again_later_with_its_failure_and_the_job_carries_on()
    {
        var harness = new Harness(NotFound("The provider did not answer.", Failure(IntegrationFailureKind.Unavailable)));

        var message = await harness.RunAsync();

        var attempt = Assert.Single(harness.Attempts);
        Assert.StartsWith("rejected: The provider did not answer.", attempt);
        harness.Subtitles.Verify(s => s.RecordAttemptAsync(
            MediaKind.Movie, "movie-1", "en", It.IsAny<string?>(), TimeSpan.FromHours(6), It.IsAny<CancellationToken>(),
            It.Is<IntegrationFailure?>(f => f != null && f.Kind == IntegrationFailureKind.Unavailable)), Times.Once);
        Assert.False(string.IsNullOrWhiteSpace(message));
    }

    [Fact]
    public async Task A_provider_whose_answer_is_malformed_is_held_for_review_and_not_blocklisted_as_a_bad_subtitle()
    {
        var harness = new Harness(NotFound("The daily quota is spent.", Failure(IntegrationFailureKind.MalformedResponse)));

        await harness.RunAsync();

        Assert.Empty(harness.Blocked);
        var attempt = Assert.Single(harness.Attempts);
        Assert.StartsWith("needs-review: The daily quota is spent.", attempt!);
    }

    [Fact]
    public async Task With_no_provider_set_up_the_search_says_so_and_does_not_queue_a_next_slice()
    {
        var harness = new Harness(NotFound("No subtitle providers are set up."));

        var message = await harness.RunAsync();

        Assert.Equal("Films wants subtitles and no providers are set up yet, so nothing could be fetched. Add one under Find & Download.", message);
        Assert.Empty(harness.Attempts);
        harness.Scheduler.Verify(s => s.EnqueueAsync(It.IsAny<EnqueueJobRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_cancelled_job_asks_no_provider_and_records_nothing()
    {
        var harness = new Harness(NotFound("none"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.RunAsync(token: new CancellationToken(true)));

        Assert.Equal(0, harness.Calls);
        Assert.Empty(harness.Attempts);
    }
}
