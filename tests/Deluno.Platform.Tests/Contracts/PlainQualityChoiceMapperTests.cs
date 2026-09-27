using Deluno.Quality;

namespace Deluno.Platform.Tests.Contracts;

/// <summary>
/// The mapping behind "Stop upgrading once a copy is: Good / Great / The best
/// available" and "Only replace when: a little / clearly / much better",
/// relative to a TRaSH profile's own minFormatScore/cutoffFormatScore/
/// minUpgradeFormatScore (#778, #938).
/// </summary>
public sealed class PlainQualityChoiceMapperTests
{
    [Theory]
    [InlineData(0, 10000, QualityCutoffChoice.Good, 2500)]
    [InlineData(0, 10000, QualityCutoffChoice.Great, 6000)]
    [InlineData(0, 10000, QualityCutoffChoice.BestAvailable, 10000)]
    [InlineData(1000, 3000, QualityCutoffChoice.Good, 1500)]
    [InlineData(1000, 3000, QualityCutoffChoice.Great, 2200)]
    [InlineData(1000, 3000, QualityCutoffChoice.BestAvailable, 3000)]
    public void Every_cutoff_level_maps_to_a_score_between_the_guides_floor_and_ceiling(
        int guideMin, int guideCutoff, QualityCutoffChoice choice, int expected)
    {
        Assert.Equal(expected, PlainQualityChoiceMapper.MapCutoffScore(choice, guideMin, guideCutoff));
    }

    [Fact]
    public void Cutoff_levels_are_ordered_good_to_best()
    {
        var good = PlainQualityChoiceMapper.MapCutoffScore(QualityCutoffChoice.Good, 0, 10000);
        var great = PlainQualityChoiceMapper.MapCutoffScore(QualityCutoffChoice.Great, 0, 10000);
        var best = PlainQualityChoiceMapper.MapCutoffScore(QualityCutoffChoice.BestAvailable, 0, 10000);

        Assert.True(good < great);
        Assert.True(great < best);
    }

    [Theory]
    [InlineData(QualityUpgradeGainChoice.ALittleBetter, 1, 1)]
    [InlineData(QualityUpgradeGainChoice.ClearlyBetter, 1, 3)]
    [InlineData(QualityUpgradeGainChoice.MuchBetter, 1, 6)]
    public void Every_upgrade_gain_level_scales_the_guides_own_minimum(
        QualityUpgradeGainChoice choice, int guideMinUpgrade, int expected)
    {
        Assert.Equal(expected, PlainQualityChoiceMapper.MapUpgradeGainScore(choice, guideMinUpgrade));
    }

    [Fact]
    public void Never_grab_bad_off_means_no_minimum()
    {
        Assert.Null(PlainQualityChoiceMapper.MapMinScore(neverGrabBad: false, guideMinFormatScore: 550));
    }

    [Fact]
    public void Never_grab_bad_on_takes_the_guides_own_minimum()
    {
        Assert.Equal(550, PlainQualityChoiceMapper.MapMinScore(neverGrabBad: true, guideMinFormatScore: 550));
    }

    [Fact]
    public void Never_grab_bad_on_with_no_guide_minimum_is_still_no_opinion()
    {
        Assert.Null(PlainQualityChoiceMapper.MapMinScore(neverGrabBad: true, guideMinFormatScore: 0));
    }

    [Theory]
    [InlineData(2500, QualityCutoffChoice.Good)]
    [InlineData(6000, QualityCutoffChoice.Great)]
    [InlineData(10000, QualityCutoffChoice.BestAvailable)]
    public void A_stored_cutoff_number_rounds_back_to_its_plain_level(int storedScore, QualityCutoffChoice expected)
    {
        Assert.Equal(expected, PlainQualityChoiceMapper.NearestCutoffChoice(storedScore, 0, 10000));
    }

    [Theory]
    [InlineData(1, QualityUpgradeGainChoice.ALittleBetter)]
    [InlineData(3, QualityUpgradeGainChoice.ClearlyBetter)]
    [InlineData(6, QualityUpgradeGainChoice.MuchBetter)]
    public void A_stored_upgrade_gain_number_rounds_back_to_its_plain_level(int storedScore, QualityUpgradeGainChoice expected)
    {
        Assert.Equal(expected, PlainQualityChoiceMapper.NearestUpgradeGainChoice(storedScore, 1));
    }

    [Fact]
    public void Labels_are_plain_words_not_numbers()
    {
        Assert.Equal("Good", PlainQualityChoiceMapper.CutoffLabel(QualityCutoffChoice.Good));
        Assert.Equal("Great", PlainQualityChoiceMapper.CutoffLabel(QualityCutoffChoice.Great));
        Assert.Equal("the best available", PlainQualityChoiceMapper.CutoffLabel(QualityCutoffChoice.BestAvailable));
        Assert.Equal("a little better", PlainQualityChoiceMapper.UpgradeGainLabel(QualityUpgradeGainChoice.ALittleBetter));
        Assert.Equal("clearly better", PlainQualityChoiceMapper.UpgradeGainLabel(QualityUpgradeGainChoice.ClearlyBetter));
        Assert.Equal("much better", PlainQualityChoiceMapper.UpgradeGainLabel(QualityUpgradeGainChoice.MuchBetter));
    }
}
