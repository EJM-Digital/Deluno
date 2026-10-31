using System.Text;
using Deluno.Contracts;
using Deluno.Integrations.Subtitles;
using Deluno.Integrations.Subtitles.Providers;
using static Deluno.Integrations.Tests.Subtitles.ProviderStubs;

namespace Deluno.Integrations.Tests.Subtitles;

/// <summary>
/// The small pieces of the subtitle code the larger tests walk past: the search
/// words, held-credential check, provider lookup and the file-cleaning edge cases. #1268.
/// </summary>
public sealed class SubtitleSmallPiecesTests
{
    [Theory]
    [InlineData("Show", null, 1, 2, true, "Show S01E02")]
    [InlineData("Show", null, null, 2, true, "Show")]
    [InlineData("Show", null, 1, null, true, "Show")]
    [InlineData("Dune", 2021, null, null, false, "Dune 2021")]
    [InlineData("Dune", null, null, null, false, "Dune")]
    public void The_search_words_are_the_title_with_the_season_and_episode_or_the_year(
        string title, int? year, int? season, int? episode, bool isEpisode, string expected)
    {
        var request = new SubtitleSearchRequest(title, year, season, episode, null, null, null, [], isEpisode);

        Assert.Equal(expected, request.Query);
    }

    [Theory]
    [InlineData(null, null, null, false)]
    [InlineData(" ", " ", " ", false)]
    [InlineData("u", null, null, true)]
    [InlineData(null, "p", null, true)]
    [InlineData(null, null, "k", true)]
    public void Credentials_count_only_when_something_non_blank_is_held(string? user, string? password, string? key, bool expected)
    {
        Assert.Equal(expected, new SubtitleProviderCredentials(user, password, key).HasAny);
    }

    [Fact]
    public void A_provider_is_found_by_its_key_ignoring_case_and_spaces_and_not_by_a_blank_one()
    {
        var registry = new SubtitleProviderRegistry([new GestdownSubtitleProvider(Factory(_ => Json("{}")))]);

        Assert.NotNull(registry.Find(" GESTDOWN "));
        Assert.Null(registry.Find("nothing"));
        Assert.Null(registry.Find("  "));
        Assert.Null(registry.Find(null));
    }

    private static string Cleaned(string text, SubtitleContentModificationPolicy policy, bool bom = false)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bom)
        {
            bytes = [0xEF, 0xBB, 0xBF, .. bytes];
        }

        return Encoding.UTF8.GetString(SubtitleContentModifier.Apply(bytes, policy).Content).Replace("\r\n", "\n");
    }

    private const string Timing = "1\n00:00:01,000 --> 00:00:03,000\n";

    [Fact]
    public void A_digit_wedged_in_a_word_becomes_the_letter_in_the_case_of_its_neighbours()
    {
        var policy = new SubtitleContentModificationPolicy(FixOcrErrors: true);

        Assert.Equal(Timing + "NOBODY Nobody alb\n", Cleaned(Timing + "N0BODY N0body a1b\n", policy));
        Assert.Equal(Timing + "Alb ALB\n", Cleaned(Timing + "A1b A1B\n", policy));
    }

    [Fact]
    public void Every_kind_of_emoji_goes_and_ordinary_symbols_stay()
    {
        var policy = new SubtitleContentModificationPolicy(RemoveEmoji: true);
        var text = Timing + "a\U0001F600b☀c⌚d‍e⃣f️gé &\n";

        Assert.Equal(Timing + "abcdefgé &\n", Cleaned(text, policy));
        Assert.Equal(Timing + "plain\n", Cleaned(Timing + "plain\n", policy));
    }

    [Fact]
    public void A_byte_order_mark_is_dropped_and_digits_that_are_not_a_cue_number_are_text()
    {
        var policy = new SubtitleContentModificationPolicy(NormalizeWhitespace: true);

        Assert.Equal(Timing + "two words\n\n42", Cleaned(Timing + "two   words\n\n42", policy, bom: true));
        Assert.Equal(Timing + "line\n7\nmore text\n", Cleaned(Timing + "line\n7\nmore   text\n", policy));
    }

    [Fact]
    public void Upper_case_is_fixed_only_for_a_line_of_six_letters_or_more_with_none_in_lower_case()
    {
        var policy = new SubtitleContentModificationPolicy(FixAllUppercase: true);

        Assert.Equal(Timing + "SHORT\n", Cleaned(Timing + "SHORT\n", policy));
        Assert.Equal(Timing + "Mixed CASE here\n", Cleaned(Timing + "Mixed CASE here\n", policy));
        Assert.Equal(Timing + "123 Hello there\n", Cleaned(Timing + "123 HELLO THERE\n", policy));
    }
}
