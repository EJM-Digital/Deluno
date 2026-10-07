using Deluno.Contracts;
using Deluno.Media;

namespace Deluno.Persistence.Tests.Media;

/// <summary>
/// Why a title will not download, said plainly.
///
/// <para>The mechanism these describe is the one Radarr gets right and explains
/// badly: a blocklisted release "will not be automatically downloaded ever
/// again" and stays that way "forever unless you manually remove them". Without
/// something like it, a failed import becomes an endless re-download loop.
/// With it and no explanation, a title simply never arrives and nobody can find
/// out why.</para>
///
/// <para>So what is asserted here is not only that a blocker is detected, but
/// that the sentence it produces would be useful to a person reading it — it
/// names the thing holding the record, and says whether they can do anything
/// about it.</para>
/// </summary>
public sealed class AcquisitionBlockerReaderTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-05T12:00:00Z");

    [Fact]
    public void A_title_with_nothing_in_the_way_says_so()
    {
        var answer = Read(Wanted(hasFile: false));

        Assert.True(answer.NothingIsBlocking);
        Assert.Empty(answer.Blockers);
        Assert.False(answer.CanForce);
        Assert.Contains("Nothing is stopping", answer.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// The commonest one, and the one that should not offer a force: a title
    /// already held at its target is not a problem to be overridden.
    /// </summary>
    [Fact]
    public void A_title_already_held_at_its_target_is_explained_and_not_forceable()
    {
        var answer = Read(Wanted(hasFile: true, cutoffMet: true));

        var blocker = Assert.Single(answer.Blockers);
        Assert.Equal(AcquisitionBlockerKinds.AlreadyHeld, blocker.Kind);
        Assert.False(blocker.CanClear);
        Assert.False(answer.CanForce);
        Assert.Contains("already here", blocker.Summary, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The lab case. A client holding the release is invisible to the user and
    /// is the reason a re-send does nothing, so it has to name the client.
    /// </summary>
    [Fact]
    public void A_download_client_that_already_has_the_release_names_the_client()
    {
        var answer = Read(Wanted(hasFile: false), clientHoldingRelease: "qBittorrent");

        var blocker = Assert.Single(answer.Blockers);
        Assert.Equal(AcquisitionBlockerKinds.DownloadInFlight, blocker.Kind);
        Assert.Equal("qBittorrent", blocker.Source);
        // #1138: a download that has started is said as progress, not as a reason it will not download.
        Assert.Equal("Downloading in qBittorrent.", blocker.Summary);
        Assert.True(blocker.CanClear);
        Assert.True(answer.CanForce);
        // Clearing this means touching something that is not Deluno, so the
        // effect has to say which thing.
        Assert.Contains("qBittorrent", blocker.ClearEffect!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_processor_still_holding_the_file_is_named_too()
    {
        var answer = Read(Wanted(hasFile: false), processorHoldingFile: "Weir");

        var blocker = Assert.Single(answer.Blockers);
        Assert.Equal(AcquisitionBlockerKinds.ProcessorHoldingFile, blocker.Kind);
        Assert.Equal("Weir", blocker.Source);
        Assert.True(blocker.CanClear);
    }

    [Fact]
    public void An_exclusion_is_reported_as_the_deliberate_thing_it_is()
    {
        var answer = Read(Wanted(hasFile: false), isImportExcluded: true);

        var blocker = Assert.Single(answer.Blockers);
        Assert.Equal(AcquisitionBlockerKinds.ImportExcluded, blocker.Kind);
        Assert.True(blocker.CanClear);
        Assert.Contains("removed with the exclusion option", blocker.Detail, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A retry window is a reason, and a date the user can read is the whole
    /// value of reporting it.
    /// </summary>
    [Fact]
    public void A_retry_delay_reports_when_it_lifts()
    {
        var answer = Read(Wanted(hasFile: false) with
        {
            NextEligibleSearchUtc = Now.AddHours(6),
            LastSearchResult = "No candidates met the profile."
        });

        var blocker = Assert.Single(answer.Blockers);
        Assert.Equal(AcquisitionBlockerKinds.SearchDeferred, blocker.Kind);
        Assert.Contains("in about 6 hours", blocker.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("2026-", blocker.Summary, StringComparison.Ordinal);
        Assert.Equal(Now.AddHours(6), blocker.UntilUtc);
        Assert.Contains("No candidates met the profile.", blocker.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// A window that has already passed is not a blocker. Reporting it would be
    /// noise, and noise is what stops people reading these at all.
    /// </summary>
    [Fact]
    public void A_retry_delay_that_has_already_passed_is_not_reported()
    {
        var answer = Read(Wanted(hasFile: false) with { NextEligibleSearchUtc = Now.AddHours(-1) });

        Assert.True(answer.NothingIsBlocking);
    }

    [Fact]
    public void A_title_that_is_not_out_yet_is_explained_rather_than_searched_for()
    {
        var answer = Read(Wanted(hasFile: false) with { AvailableUtc = Now.AddDays(30) });

        var blocker = Assert.Single(answer.Blockers);
        Assert.Equal(AcquisitionBlockerKinds.NotYetAvailable, blocker.Kind);
        Assert.False(blocker.CanClear);
    }

    /// <summary>
    /// Several at once is the realistic case, and the summary must stay one
    /// readable sentence rather than a list nobody finishes.
    /// </summary>
    [Fact]
    public void Several_blockers_are_summarised_in_one_sentence_that_counts_them()
    {
        var answer = Read(
            Wanted(hasFile: false) with { NextEligibleSearchUtc = Now.AddHours(2) },
            clientHoldingRelease: "qBittorrent",
            processorHoldingFile: "Weir",
            isImportExcluded: true);

        Assert.Equal(4, answer.Blockers.Count);
        Assert.True(answer.CanForce);
        Assert.Contains("3 other reasons", answer.Summary, StringComparison.Ordinal);
        Assert.Contains("4 can be overridden", answer.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// And when nothing can be cleared, the answer must not offer a button that
    /// would do nothing.
    /// </summary>
    [Fact]
    public void An_answer_with_no_clearable_blockers_does_not_offer_a_force()
    {
        var answer = Read(
            Wanted(hasFile: true, cutoffMet: true) with { AvailableUtc = Now.AddDays(10) });

        Assert.Equal(2, answer.Blockers.Count);
        Assert.False(answer.CanForce);
        Assert.Contains("none of which Deluno can clear", answer.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// A title no library tracks yet has no wanted row, and asking about it
    /// must answer rather than throw.
    /// </summary>
    [Fact]
    public void A_title_with_no_wanted_row_is_answered_not_refused()
    {
        var answer = AcquisitionBlockerReader.Read(
            "movie-1", "movies", "Arrival", null, null, null, false, false, Now);

        Assert.True(answer.NothingIsBlocking);
    }

    // ------------------------------------------------------------------ helpers

    private static AcquisitionBlockersResponse Read(
        MediaWantedItem wanted,
        string? clientHoldingRelease = null,
        string? processorHoldingFile = null,
        bool isImportExcluded = false,
        bool nextSearchSkipped = false)
        => AcquisitionBlockerReader.Read(
            "movie-1",
            "movies",
            "Arrival",
            wanted,
            clientHoldingRelease,
            processorHoldingFile,
            isImportExcluded,
            nextSearchSkipped,
            Now);

    private static MediaWantedItem Wanted(bool hasFile, bool cutoffMet = false)
        => new(
            "movie-1",
            "Arrival",
            2016,
            "tt2543164",
            "library-movies",
            hasFile ? WantedStatuses.Covered : WantedStatuses.Missing,
            hasFile ? "Held." : "Not here yet.",
            hasFile,
            hasFile ? "WEB 1080p" : null,
            "WEB 1080p",
            cutoffMet,
            null,
            null,
            null,
            null,
            false,
            null,
            Now);

    /// <summary>
    /// The case this whole feature was built for, and the last one it learned
    /// to answer.
    ///
    /// <para>A title that downloaded, imported and was then removed leaves a
    /// *completed* dispatch — and the source that finds blockers deliberately
    /// ignores completed dispatches, because a download that imported is
    /// history rather than an obstacle. Right for everything else, and wrong
    /// for exactly this: the file has gone, so that completed download is the
    /// reason the client will refuse the next attempt.</para>
    /// </summary>
    [Fact]
    public void A_title_fetched_before_and_no_longer_held_says_so()
    {
        var answer = AcquisitionBlockerReader.Read(
            "movie-1",
            "movies",
            "Arrival",
            Wanted(hasFile: false),
            clientHoldingRelease: null,
            processorHoldingFile: null,
            isImportExcluded: false,
            nextSearchSkipped: false,
            Now,
            previouslyFetchedFrom: "qBittorrent",
            previouslyFetchedUtc: DateTimeOffset.Parse("2026-09-03T10:00:00Z"));

        var blocker = Assert.Single(answer.Blockers);
        Assert.Equal(AcquisitionBlockerKinds.PreviouslyDownloaded, blocker.Kind);
        Assert.Contains("qBittorrent", blocker.Summary, StringComparison.Ordinal);
        Assert.Contains("3 September 2026", blocker.Summary, StringComparison.Ordinal);
        Assert.True(blocker.CanClear);
        Assert.True(answer.CanForce);
    }

    /// <summary>
    /// It claims only what Deluno knows. It cannot see a download client's
    /// memory without asking, and the client may be unreachable or already
    /// cleared by hand — so the detail says "may", and an override is offered
    /// rather than a fact asserted about somebody else's state.
    /// </summary>
    [Fact]
    public void It_does_not_claim_the_client_still_holds_the_release()
    {
        var answer = AcquisitionBlockerReader.Read(
            "movie-1", "movies", "Arrival", Wanted(hasFile: false),
            null, null, false, false, Now,
            previouslyFetchedFrom: "SABnzbd",
            previouslyFetchedUtc: Now);

        var blocker = Assert.Single(answer.Blockers);
        Assert.Contains("may already be clear", blocker.Detail, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// And it stays quiet when the file is here. Having fetched something
    /// before is only interesting once it has gone.
    /// </summary>
    [Fact]
    public void A_title_that_is_still_held_does_not_mention_having_fetched_it()
    {
        var answer = AcquisitionBlockerReader.Read(
            "movie-1", "movies", "Arrival", Wanted(hasFile: true),
            null, null, false, false, Now,
            previouslyFetchedFrom: "qBittorrent",
            previouslyFetchedUtc: Now);

        Assert.DoesNotContain(answer.Blockers, blocker => blocker.Kind == AcquisitionBlockerKinds.PreviouslyDownloaded);
    }

    /// <summary>
    /// Suppressed while something is downloading. If a client has the release
    /// in hand right now, "you fetched this once" is not the answer to why it
    /// is not arriving — and two blockers naming the same client would read as
    /// two problems.
    /// </summary>
    [Fact]
    public void A_download_in_flight_silences_the_older_fetch()
    {
        var answer = AcquisitionBlockerReader.Read(
            "movie-1", "movies", "Arrival", Wanted(hasFile: false),
            clientHoldingRelease: "qBittorrent",
            processorHoldingFile: null,
            isImportExcluded: false,
            nextSearchSkipped: false,
            Now,
            previouslyFetchedFrom: "qBittorrent",
            previouslyFetchedUtc: Now);

        var blocker = Assert.Single(answer.Blockers);
        Assert.Equal(AcquisitionBlockerKinds.DownloadInFlight, blocker.Kind);
    }

    /// <summary>
    /// The blocklist answering for itself.
    ///
    /// <para>Refusing releases is a mechanism that can become the problem:
    /// refuse enough of them and a search finds nothing, with the reason
    /// sitting in a list nobody thought to open. So it is stated wherever
    /// somebody asks why a title is not arriving.</para>
    /// </summary>
    [Fact]
    public void Refused_releases_for_a_title_are_stated_where_somebody_is_asking_why()
    {
        var answer = AcquisitionBlockerReader.Read(
            "movie-1", "movies", "Arrival", Wanted(hasFile: false),
            null, null, false, false, Now,
            blockedReleaseCount: 3);

        var blocker = Assert.Single(answer.Blockers);
        Assert.Equal(AcquisitionBlockerKinds.ReleasesBlocked, blocker.Kind);
        Assert.Contains("3 releases", blocker.Summary, StringComparison.Ordinal);
        Assert.True(blocker.CanClear);
        Assert.Contains("Un-refuses all 3", blocker.ClearEffect!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Stated, not blamed. Deluno cannot know from here whether a good
    /// candidate still exists — that needs a search — so the wording reports
    /// the fact rather than claiming to be the cause.
    /// </summary>
    [Fact]
    public void It_reports_the_refusals_without_claiming_they_are_the_cause()
    {
        var answer = AcquisitionBlockerReader.Read(
            "movie-1", "movies", "Arrival", Wanted(hasFile: false),
            null, null, false, false, Now,
            blockedReleaseCount: 1);

        var blocker = Assert.Single(answer.Blockers);
        Assert.Contains("one fewer option", blocker.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("If every copy", blocker.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// And it is silent about a title already held. Refusing a bad copy of a
    /// film you have is not something anybody needs telling.
    /// </summary>
    [Fact]
    public void Refusals_are_not_mentioned_for_a_title_that_is_already_here()
    {
        var answer = AcquisitionBlockerReader.Read(
            "movie-1", "movies", "Arrival", Wanted(hasFile: true, cutoffMet: true),
            null, null, false, false, Now,
            blockedReleaseCount: 3);

        Assert.DoesNotContain(answer.Blockers, blocker => blocker.Kind == AcquisitionBlockerKinds.ReleasesBlocked);
    }
}
