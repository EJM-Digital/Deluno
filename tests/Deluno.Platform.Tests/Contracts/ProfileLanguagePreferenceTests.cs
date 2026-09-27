using Deluno.Quality;

namespace Deluno.Platform.Tests.Contracts;

/// <summary>"Preferred language" for a quality profile (#769).</summary>
public sealed class ProfileLanguagePreferenceTests
{
    [Fact]
    public void Having_no_opinion_is_the_starting_state_and_stores_nothing()
    {
        var none = new ProfileLanguagePreference();

        Assert.True(none.IsEmpty);
        Assert.Equal(string.Empty, ProfileLanguagePreferenceCodec.Serialize(none));
        Assert.Null(ProfileLanguagePreferenceCodec.Deserialize(string.Empty));
    }

    [Fact]
    public void Codes_are_tidied_and_deduplicated()
    {
        var preference = new ProfileLanguagePreference(["EN", "en", " fr "]).Normalize();

        Assert.Equal(["en", "fr"], preference.PreferredLanguages);
    }

    [Fact]
    public void Requiring_a_language_nobody_named_is_read_as_no_opinion()
    {
        var preference = new ProfileLanguagePreference(PreferredLanguages: [], RequireLanguage: true).Normalize();

        Assert.True(preference.IsEmpty);
        Assert.False(preference.RequireLanguage);
    }

    [Fact]
    public void A_real_preference_round_trips()
    {
        var preference = new ProfileLanguagePreference(["en", "original"], RequireLanguage: true);

        var json = ProfileLanguagePreferenceCodec.Serialize(preference);
        Assert.NotEqual(string.Empty, json);
        var roundTripped = ProfileLanguagePreferenceCodec.Deserialize(json);
        Assert.NotNull(roundTripped);
        Assert.Equal(preference.PreferredLanguages, roundTripped!.PreferredLanguages);
        Assert.Equal(preference.RequireLanguage, roundTripped.RequireLanguage);
    }

    [Fact]
    public void An_unreadable_answer_leaves_the_profile_with_no_language_opinion()
    {
        Assert.Null(ProfileLanguagePreferenceCodec.Deserialize("{not json"));
    }

    [Theory]
    [InlineData("en", "English")]
    [InlineData("fr", "French")]
    [InlineData("original", "Original language")]
    public void Labels_match_the_same_traits_the_typed_plan_uses(string code, string expectedLabel)
    {
        Assert.Equal(expectedLabel, ProfileLanguageCodes.Label(code));
    }
}
