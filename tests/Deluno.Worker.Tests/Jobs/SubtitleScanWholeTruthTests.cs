using Deluno.Contracts;
using Deluno.Media;
using Deluno.Worker.Jobs;

namespace Deluno.Worker.Tests.Jobs;

/// <summary>
/// What a subtitle scan hands over as the whole truth about a file.
///
/// <para>These exist because of what is on the other end of that list.
/// <c>RecordScanAsync</c> replaces everything it was told and deletes anything
/// it was not — which is right, and is how a subtitle deleted from disk finally
/// corrects itself — but it means an incomplete list is a destructive one.</para>
///
/// <para><b>The rig cannot check this.</b> Its videos were remuxed with
/// <c>-sn</c> and hold no embedded tracks at all, so the failure this guards
/// against would not show there: every embedded subtitle in a real library
/// quietly deleted twelve hours after it was found, with the bar going red on
/// files that had nothing wrong with them.</para>
/// </summary>
public sealed class SubtitleScanWholeTruthTests
{
    [Fact]
    public void A_full_read_is_the_whole_truth_on_its_own()
    {
        var found = new[] { Sidecar("en"), Embedded("fr") };

        // Nothing carried forward: this pass looked everywhere, so anything it
        // did not find is genuinely gone.
        var truth = LibrarySubtitleScanJobHandler.WholeTruth(
            videoWasProbed: true,
            recorded: [Embedded("de")],
            found: found);

        Assert.Equal(found, truth);
    }

    [Fact]
    public void A_folder_only_read_keeps_the_tracks_inside_the_container()
    {
        var truth = LibrarySubtitleScanJobHandler.WholeTruth(
            videoWasProbed: false,
            recorded: [Embedded("de"), Sidecar("en")],
            found: [Sidecar("en")]);

        // The German track is still in the file — nobody looked, which is not
        // the same as it being gone.
        Assert.Contains(truth, row => row.Language == "de" && row.Source == SubtitleSources.Embedded);
        Assert.Contains(truth, row => row.Language == "en" && row.Source == SubtitleSources.External);
    }

    [Fact]
    public void A_folder_only_read_does_not_resurrect_a_sidecar_that_has_been_deleted()
    {
        // The whole point of the cadence. English was recorded as a file beside
        // the video; the folder no longer has it.
        var truth = LibrarySubtitleScanJobHandler.WholeTruth(
            videoWasProbed: false,
            recorded: [Embedded("de"), Sidecar("en")],
            found: []);

        Assert.DoesNotContain(truth, row => row.Language == "en");
        Assert.Single(truth);
    }

    [Fact]
    public void A_subtitle_beside_the_video_beats_a_track_welded_into_it()
    {
        var truth = LibrarySubtitleScanJobHandler.WholeTruth(
            videoWasProbed: false,
            recorded: [Embedded("en")],
            found: [Sidecar("en")]);

        // Both rows share the upsert's key, so the later one wins and it has to
        // be the sidecar: a file can be swapped, corrected or upgraded and a
        // track inside the container cannot.
        Assert.Equal(SubtitleSources.External, truth[^1].Source);
    }

    private static MediaSubtitleRow Embedded(string language)
        => new(language, SubtitleSources.Embedded, false, false, null, 2, "subrip", null);

    private static MediaSubtitleRow Sidecar(string language)
        => new(language, SubtitleSources.External, false, false, $@"D:\Media\Film.{language}.srt", null, "srt", null);

    private static MediaSubtitleRow Fetched(string language, string sourceKey)
        => new(language, SubtitleSources.Fetched, false, false, $@"D:\Media\Film.{language}.srt", null, "srt",
            "gestdown", MatchRung: 0, SourceKey: sourceKey);
}

/// <summary>
/// A fetched subtitle that has gone missing from the folder (#895): Deluno's
/// own work disappearing is you rejecting it, not the file forgetting itself.
/// </summary>
public sealed class SubtitleScanRemovedTests
{
    [Fact]
    public void A_fetched_subtitle_no_longer_beside_the_video_is_removed()
    {
        var recorded = new[] { Fetched("en", "gestdown|abc") };

        var removed = LibrarySubtitleScanJobHandler.Removed(recorded, rows: []);

        var row = Assert.Single(removed);
        Assert.Equal("en", row.Language);
        Assert.Equal("gestdown|abc", row.SourceKey);
    }

    [Fact]
    public void A_fetched_subtitle_still_present_is_not_removed()
    {
        var recorded = new[] { Fetched("en", "gestdown|abc") };
        var rows = new[] { Fetched("en", "gestdown|abc") };

        Assert.Empty(LibrarySubtitleScanJobHandler.Removed(recorded, rows));
    }

    /// <summary>
    /// An embedded or sidecar track Deluno never fetched has no candidate to
    /// blocklist, and disappearing is not you rejecting a choice Deluno made.
    /// </summary>
    [Fact]
    public void A_track_Deluno_did_not_fetch_is_never_reported_as_removed()
    {
        var recorded = new[] { Embedded("de"), Sidecar("fr") };

        Assert.Empty(LibrarySubtitleScanJobHandler.Removed(recorded, rows: []));
    }

    /// <summary>A fetched row before this feature existed has no candidate identity to refuse again.</summary>
    [Fact]
    public void A_fetched_row_with_no_source_key_is_an_honest_gap_not_a_removal()
    {
        var recorded = new[]
        {
            new MediaSubtitleRow("en", SubtitleSources.Fetched, false, false, @"D:\Media\Film.en.srt", null, "srt",
                "gestdown", MatchRung: 0, SourceKey: null)
        };

        Assert.Empty(LibrarySubtitleScanJobHandler.Removed(recorded, rows: []));
    }

    private static MediaSubtitleRow Embedded(string language)
        => new(language, SubtitleSources.Embedded, false, false, null, 2, "subrip", null);

    private static MediaSubtitleRow Sidecar(string language)
        => new(language, SubtitleSources.External, false, false, $@"D:\Media\Film.{language}.srt", null, "srt", null);

    private static MediaSubtitleRow Fetched(string language, string sourceKey)
        => new(language, SubtitleSources.Fetched, false, false, $@"D:\Media\Film.{language}.srt", null, "srt",
            "gestdown", MatchRung: 0, SourceKey: sourceKey);
}
