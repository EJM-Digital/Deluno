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
        Assert.Contains("2026-09-05 18:00:00Z", blocker.Summary, StringComparison.Ordinal);
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
}
