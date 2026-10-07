using Deluno.Quality.ReleasePreferences;

namespace Deluno.Persistence.Tests.Quality;

/// <summary>
/// A custom format is stored as "Custom rule · name". That label is an internal
/// handle and must never be read out in a sentence a person sees (#1138).
/// </summary>
public sealed class CustomRuleWordingTests
{
    [Theory]
    [InlineData("Custom rule · WEB Tier 01", "your preferred release groups")]
    [InlineData("Custom rule · Trusted group", "Trusted group")]
    [InlineData("Audio format", "Audio format")]
    public void A_custom_rule_is_named_in_plain_words(string dimension, string expected)
        => Assert.Equal(expected, ReleasePreferenceEvaluator.PlainDimension(dimension));

    [Fact]
    public void The_plain_name_can_start_a_sentence()
        => Assert.Equal("Your preferred release groups", ReleasePreferenceEvaluator.PlainDimension("Custom rule · WEB Tier 01", capitalise: true));
}
