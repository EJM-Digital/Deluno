using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Contracts;
using Deluno.Platform.Contracts;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>#1182, #1189, #1200: one reading of "sent again", "import ran", "import in flight" and "source gone" for the queue and the planner alike.</summary>
public sealed class DownloadAttemptsTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-20T10:00:00Z");
    private const string Gone = "The download was gone before Weir could clean it.";

    [Theory]
    [InlineData("Film.2020.1080p-GRP", "Film.2020.1080p-GRP.mkv", true)]
    [InlineData("Film.2020.1080p-GRP.MKV", "film.2020.1080p-grp", true)]
    [InlineData("Show.S01E01.nzb", "Show.S01E01", true)]
    [InlineData("  Film.2020  ", "Film.2020", true)]
    [InlineData("Film.2020.mp4", "Film.2020.mkv", true)]
    [InlineData("Film.2020", "Film.2021", false)]
    [InlineData("Film.2020.txt", "Film.2020", false)]
    [InlineData(".mkv", ".mkv", true)]
    [InlineData(".mkv", ".mp4", false)]
    [InlineData(".mkv", "x", false)]
    [InlineData(null, "Film", false)]
    [InlineData("Film", null, false)]
    [InlineData(" ", "Film", false)]
    [InlineData("Film", "  ", false)]
    public void Two_names_are_one_release_when_only_case_and_a_video_or_nzb_extension_differ(string? left, string? right, bool same)
    {
        Assert.Equal(same, DownloadAttempts.SameRelease(left, right));
    }

    private static DownloadDispatchItem Dispatch(string client, string release, DateTimeOffset created, string? grab, string? import)
        => new("d" + created.Ticks, "lib", "movies", "movie", "m1", release, "Indexer", client, "Client", "completed", null, created,
            grab, null, null, null, null, null, null, null, null, import, null, null, null, null, null, null);

    [Fact]
    public void A_send_after_the_cut_off_that_the_client_took_and_nobody_imported_is_a_new_attempt()
    {
        var sends = new[] { Dispatch("c1", "Film.2020", Now, "succeeded", null) };

        Assert.True(DownloadAttempts.SentAgainSince(sends, "c1", "Film.2020.mkv", Now.AddMinutes(-1)));
        Assert.True(DownloadAttempts.SentAgainSince(sends, "C1", "film.2020", Now.AddMinutes(-1)));
        Assert.False(DownloadAttempts.SentAgainSince(sends, "c1", "Film.2020", Now));
        Assert.False(DownloadAttempts.SentAgainSince(sends, "c2", "Film.2020", Now.AddMinutes(-1)));
        Assert.False(DownloadAttempts.SentAgainSince(sends, "c1", "Other", Now.AddMinutes(-1)));
        Assert.False(DownloadAttempts.SentAgainSince([], "c1", "Film.2020", Now.AddMinutes(-1)));
    }

    [Theory]
    [InlineData("failed", null)]
    [InlineData(null, null)]
    [InlineData("succeeded", "imported")]
    [InlineData("succeeded", "failed")]
    public void A_send_that_was_refused_or_already_imported_is_not_a_new_attempt(string? grab, string? import)
    {
        var sends = new[] { Dispatch("c1", "Film.2020", Now, grab, import) };

        Assert.False(DownloadAttempts.SentAgainSince(sends, "c1", "Film.2020", Now.AddMinutes(-1)));
    }

    [Fact]
    public void Only_the_latest_send_counts_whatever_the_order_of_the_list()
    {
        var sends = new[]
        {
            Dispatch("c1", "Film.2020", Now.AddHours(-2), "succeeded", null),
            Dispatch("c1", "Film.2020", Now, "succeeded", "imported"),
            Dispatch("c1", "Film.2020", Now.AddHours(-1), "succeeded", null)
        };

        Assert.False(DownloadAttempts.SentAgainSince(sends, "c1", "Film.2020", Now.AddHours(-3)));
    }

    private static JobQueueItem Job(string status, DateTimeOffset? completed, DateTimeOffset? lastAttempt, DateTimeOffset created)
        => new("j1", "filesystem.import.execute", "test", status, null, 0, created, created, null, completed, null, null, null, null, null, null, null, 3, lastAttempt, null);

    [Theory]
    [InlineData("failed", true)]
    [InlineData("DEAD-LETTER", true)]
    [InlineData("completed", false)]
    [InlineData("queued", false)]
    public void Only_a_failed_or_dead_lettered_import_ended_in_failure(string status, bool failed)
    {
        Assert.Equal(failed, DownloadAttempts.ImportJobFailedSince(Job(status, Now, null, Now.AddDays(-1)), Now.AddMinutes(-1)));
    }

    [Fact]
    public void A_failure_counts_by_when_it_ended_then_when_it_last_tried_then_when_it_was_made_and_at_the_cut_off_itself()
    {
        var cut = Now;

        Assert.True(DownloadAttempts.ImportJobFailedSince(Job("failed", cut, null, cut.AddDays(-9)), cut));
        Assert.False(DownloadAttempts.ImportJobFailedSince(Job("failed", cut.AddTicks(-1), cut.AddDays(1), cut.AddDays(1)), cut));
        Assert.True(DownloadAttempts.ImportJobFailedSince(Job("failed", null, cut, cut.AddDays(-9)), cut));
        Assert.False(DownloadAttempts.ImportJobFailedSince(Job("failed", null, cut.AddTicks(-1), cut.AddDays(1)), cut));
        Assert.True(DownloadAttempts.ImportJobFailedSince(Job("failed", null, null, cut), cut));
        Assert.False(DownloadAttempts.ImportJobFailedSince(Job("failed", null, null, cut.AddTicks(-1)), cut));
    }

    private static ProcessorHandoffItem Handoff(string status, string? importJobId, string? failure = null)
        => new("h1", "lib", "movie", "client-1", "q1", "Film.2020", "/d/Film.2020", "Weir", status, null, importJobId, failure, Now, Now);

    [Theory]
    [InlineData(null, null)]
    [InlineData("  ", "completed")]
    public void A_hand_off_with_no_import_job_has_not_had_an_import_run(string? importJobId, string? jobStatus)
    {
        Assert.False(DownloadAttempts.ImportRan(Handoff(ProcessorHandoffStatuses.Completed, importJobId), jobStatus));
    }

    [Theory]
    [InlineData(ProcessorHandoffStatuses.Completed, null, true)]
    [InlineData(ProcessorHandoffStatuses.Completed, "failed", true)]
    [InlineData(ProcessorHandoffStatuses.Dismissed, "completed", true)]
    [InlineData(ProcessorHandoffStatuses.Dismissed, "COMPLETED", true)]
    [InlineData(ProcessorHandoffStatuses.Dismissed, "failed", false)]
    [InlineData(ProcessorHandoffStatuses.Dismissed, null, false)]
    [InlineData(ProcessorHandoffStatuses.Stalled, "completed", false)]
    public void An_import_ran_when_the_hand_off_completed_or_was_dismissed_just_before_its_job_completed(string status, string? jobStatus, bool ran)
    {
        Assert.Equal(ran, DownloadAttempts.ImportRan(Handoff(status, "job-1"), jobStatus));
    }

    [Theory]
    [InlineData("queued", true)]
    [InlineData("RUNNING", true)]
    [InlineData("requeued", true)]
    [InlineData("completed", false)]
    [InlineData("failed", false)]
    [InlineData(null, false)]
    public void An_import_job_is_in_flight_while_it_waits_runs_or_waits_again(string? status, bool inFlight)
    {
        Assert.Equal(inFlight, DownloadAttempts.ImportJobInFlight(status));
    }

    [Fact]
    public void A_hand_off_is_closed_as_source_gone_only_when_dismissed_with_no_import_and_the_gone_sentence()
    {
        Assert.True(DownloadAttempts.SourceGoneClosed(Handoff(ProcessorHandoffStatuses.Dismissed, null, Gone)));
        Assert.True(DownloadAttempts.SourceGoneClosed(Handoff(ProcessorHandoffStatuses.Dismissed, " ", Gone)));
        Assert.False(DownloadAttempts.SourceGoneClosed(Handoff(ProcessorHandoffStatuses.Dismissed, "job-1", Gone)));
        Assert.False(DownloadAttempts.SourceGoneClosed(Handoff(ProcessorHandoffStatuses.Dismissed, null, "Something else went wrong.")));
        Assert.False(DownloadAttempts.SourceGoneClosed(Handoff(ProcessorHandoffStatuses.Dismissed, null, null)));
        Assert.False(DownloadAttempts.SourceGoneClosed(Handoff(ProcessorHandoffStatuses.Stalled, null, Gone)));
    }
}
