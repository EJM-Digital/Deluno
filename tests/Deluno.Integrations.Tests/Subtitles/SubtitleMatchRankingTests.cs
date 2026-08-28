using Deluno.Contracts;
using Deluno.Integrations.Subtitles;

namespace Deluno.Integrations.Tests.Subtitles;

/// <summary>
/// The match ladder — which rung a subtitle sits on against the file it is for.
///
/// <para>Read out of Bazarr rather than invented. Its eleven weights decode to
/// gates with a tiebreaker tail: at the shipped 90% for an episode, the right
/// show and the right episode is 86% and <i>fails</i>; add <c>source</c> and it
/// is 93% and passes. So source is the gate, release group is above it, and
/// resolution and codecs cannot change the answer at all.</para>
///
/// <para>Deluno's cutoff is the top rung. James: <i>"we need the best method, no
/// point spreading lies about subs that may be out of sync etc etc."</i></para>
/// </summary>
public sealed class SubtitleMatchRankingTests
{
    private const string File = "Severance.S01E01.1080p.WEB.H264-TEPES.mkv";

    [Fact]
    public void The_exact_release_group_is_made_for_this_file()
    {
        Assert.Equal(
            SubtitleMatch.MadeForThisFile,
            SubtitleMatchRanking.Rank("Severance.S01E01.720p.WEB.H264-TEPES", File));
    }

    [Fact]
    public void A_different_group_from_the_same_master_is_same_source()
    {
        Assert.Equal(
            SubtitleMatch.SameSource,
            SubtitleMatchRanking.Rank("Severance.S01E01.1080p.WEB-DL.H264-NTb", File));
    }

    [Fact]
    public void A_different_master_is_only_any_release()
    {
        Assert.Equal(
            SubtitleMatch.AnyRelease,
            SubtitleMatchRanking.Rank("Severance.S01E01.1080p.BluRay.x265-NTb", File));
    }

    /// <summary>
    /// A Remux is a Blu-ray with the compression taken off — same disc, same
    /// frames, same moment the film starts — so a subtitle for one is in time for
    /// the other.
    /// </summary>
    [Fact]
    public void A_remux_and_a_bluray_are_the_same_master()
    {
        Assert.Equal(
            SubtitleMatch.SameSource,
            SubtitleMatchRanking.Rank("Dune.2021.1080p.BluRay.x264-GROUP", "Dune.2021.2160p.Remux.HDR-OTHER.mkv"));
    }

    /// <summary>
    /// Resolution and codec are one point each in Bazarr and can never turn a
    /// pass into a fail. They must not here either, or a 1080p subtitle for a
    /// 2160p file would read as a worse match than it is.
    /// </summary>
    [Fact]
    public void Resolution_and_codec_do_not_move_the_rung()
    {
        Assert.Equal(
            SubtitleMatch.MadeForThisFile,
            SubtitleMatchRanking.Rank("Severance.S01E01.2160p.WEB.x265-TEPES", File));
    }

    /// <summary>
    /// Unknown is never a match. A provider that says nothing about the release
    /// might have a perfect subtitle and Deluno cannot tell — claiming otherwise
    /// is the lie the ladder exists to stop.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("English subtitles")]
    public void Nothing_known_about_the_release_is_the_bottom_rung(string? releaseName)
    {
        Assert.Equal(SubtitleMatch.AnyRelease, SubtitleMatchRanking.Rank(releaseName, File));
    }

    /// <summary>
    /// And the same when it is the <i>file</i> Deluno knows nothing about, which
    /// happens on an import whose name carried no release information.
    /// </summary>
    [Fact]
    public void A_file_with_no_release_information_cannot_be_matched_against()
    {
        Assert.Equal(
            SubtitleMatch.AnyRelease,
            SubtitleMatchRanking.Rank("Severance.S01E01.1080p.WEB.H264-TEPES", "episode one.mkv"));
    }

    /// <summary>
    /// Gestdown puts a bare group name in its <c>version</c> field — the live API
    /// answers <c>"TEPES"</c> for Severance S01E01 — and the file-name parser
    /// looks for the trailing <c>-GROUP</c> convention, which that does not have.
    ///
    /// <para>The rig is what proved it. Without this every Gestdown subtitle
    /// would score at the bottom rung and be re-fetched for ever.</para>
    /// </summary>
    [Fact]
    public void A_bare_group_name_is_understood()
    {
        Assert.Equal(SubtitleMatch.MadeForThisFile, SubtitleMatchRanking.Rank("TEPES", File));
    }

    /// <summary>
    /// And one subtitle is often named after several releases at once — Gestdown
    /// answers <c>"WEBRip-ION10, WEBRip-ION265, WEB-AFG, ..."</c> for a single
    /// file. The best of them is the answer.
    /// </summary>
    [Fact]
    public void A_list_of_releases_is_ranked_at_its_best()
    {
        Assert.Equal(
            SubtitleMatch.MadeForThisFile,
            SubtitleMatchRanking.Rank("WEBRip-ION10, WEB-AFG, Severance.S01E01.1080p.WEB.H264-TEPES", File));

        // ...and a list with nothing better than the master still says so.
        Assert.Equal(
            SubtitleMatch.SameSource,
            SubtitleMatchRanking.Rank("WEBRip-ION10, WEB-AFG, WEB.720p-GGEZ", File));
    }

    /// <summary>
    /// A bare token that is plainly not a group name must not be read as one.
    /// </summary>
    [Fact]
    public void A_sentence_is_not_a_release_group()
    {
        Assert.Equal(
            SubtitleMatch.AnyRelease,
            SubtitleMatchRanking.Rank("TEPES version", "Some.Film.2019.1080p.BluRay-TEPES version.mkv"));
    }

    [Fact]
    public void The_cutoff_is_the_top_rung()
    {
        Assert.Equal(SubtitleMatch.MadeForThisFile, SubtitleCutoff.Rung);
        // Bazarr's shipped default decodes to SameSource, and Deluno goes past it.
        Assert.True(SubtitleCutoff.Rung > SubtitleMatch.SameSource);
    }

    [Fact]
    public void Every_rung_can_be_described_in_a_sentence()
    {
        foreach (var rung in Enum.GetValues<SubtitleMatch>())
        {
            Assert.False(string.IsNullOrWhiteSpace(SubtitleMatchRanking.Describe(rung)));
        }
    }
}
