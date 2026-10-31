using Deluno.Filesystem.Subtitles;

namespace Deluno.Persistence.Tests.Media;

/// <summary>Marking, unmarking and sliding speech masks, and what an alignment says about itself. #1268.</summary>
public sealed class SpeechMaskGapsTests
{
    private static SpeechMask Mask(int frames, params (double Start, double End)[] ranges)
    {
        var mask = new SpeechMask(frames);
        foreach (var (start, end) in ranges)
        {
            mask.Mark(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end));
        }

        return mask;
    }

    [Fact]
    public void Speech_is_marked_clipped_to_the_mask_and_taken_back_out()
    {
        var mask = Mask(1000, (-5, 2), (9, 50));

        Assert.Equal(200 + 100, mask.Population);
        Assert.True(mask[0]);
        Assert.True(mask[999]);
        Assert.False(mask[-1]);
        Assert.False(mask[1000]);
        Assert.False(mask[500]);

        mask.Unmark(TimeSpan.FromSeconds(-1), TimeSpan.FromSeconds(1));
        mask.Unmark(TimeSpan.FromSeconds(9.5), TimeSpan.FromSeconds(99));
        Assert.Equal(100 + 50, mask.Population);
        Assert.False(mask[0]);
        Assert.True(mask[150]);
        Assert.Equal(TimeSpan.FromSeconds(1.5), SpeechMask.ToTime(150));
    }

    [Fact]
    public void A_mask_finds_the_shift_that_lines_two_recordings_up_across_word_boundaries()
    {
        var audio = Mask(2000, (2, 3), (7, 9), (14, 15));
        var subtitle = Mask(2000, (3.5, 4.5), (8.5, 10.5), (15.5, 16.5));

        var found = audio.Correlate(subtitle, 300);

        Assert.Equal(-150, found.ShiftFrames);
        Assert.Equal(TimeSpan.FromSeconds(-1.5), found.Shift);
        Assert.True(found.Score > found.ScoreAtZero);
        Assert.True(found.Improvement > 0);
        Assert.True(found.Prominence > 1);
        Assert.True(found.PeakSigma > 0);

        // A whole number of 64-frame words, and a mask with an empty word.
        var wordAligned = Mask(2000, (0.64, 1.28)).Correlate(Mask(2000, (0, 0.64)), 64);
        Assert.Equal(64, wordAligned.ShiftFrames);
        Assert.Equal(0, Mask(2000).Correlate(Mask(2000, (1, 2)), 5).Score);
    }

    [Fact]
    public void An_alignment_with_nothing_to_compare_against_scores_zero_everywhere()
    {
        var gained = new SpeechAlignment(5, 10, 0, 1, 1);
        var nothing = new SpeechAlignment(0, 0, 0, 0, 0);

        Assert.Equal(1d, gained.Improvement);
        Assert.Equal(0d, nothing.Improvement);
        Assert.Equal(0d, nothing.Prominence);
        Assert.Equal(0d, nothing.PeakSigma);
        Assert.Equal(0d, new SpeechAlignment(0, 5, 10, 1, 1).Improvement);
    }
}
