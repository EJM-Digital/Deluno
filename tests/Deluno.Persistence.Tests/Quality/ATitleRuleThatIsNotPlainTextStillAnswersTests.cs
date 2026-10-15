using Deluno.Quality.ReleasePreferences;

namespace Deluno.Persistence.Tests.Quality;

/// <summary>
/// A custom format's release-title rule is plain text, a regex the linear-time engine can run, a regex it cannot
/// (lookarounds, backreferences), or a regex that is not valid. Each answers the same way every time and never throws.
/// </summary>
public sealed class ATitleRuleThatIsNotPlainTextStillAnswersTests
{
    private static bool Matches(string releaseName, string rule)
        => CustomFormatDefinitionMatcher.Evaluate(releaseName, rule, anyCondition: true, "movies").IsMatch;

    [Theory]
    [InlineData("Movie.2026.1080p.WEB-DL.DDP5.1-GRP", @"regex:(?<=\.)WEB-DL(?=\.DDP)", true)]
    [InlineData("Movie.2026.1080p.WEB-DL.AAC-GRP", @"regex:(?<=\.)WEB-DL(?=\.DDP)", false)]
    [InlineData("Movie.2026.2026.1080p-GRP", @"regex:(\d{4})\.\1", true)]
    [InlineData("Movie.2026.2025.1080p-GRP", @"regex:(\d{4})\.\1", false)]
    public void A_regex_the_linear_engine_cannot_run_is_run_by_the_backtracking_one(string name, string rule, bool expected)
        => Assert.Equal(expected, Matches(name, rule));

    [Theory]
    [InlineData("Movie.2026.1080p.x264-GRP", "x264-GRP|x265-GRP", true)]
    [InlineData("Movie.2026.1080p.x266-GRP", "x264-GRP|x265-GRP", false)]
    public void A_regex_the_linear_engine_runs_is_matched_ignoring_case(string name, string rule, bool expected)
        => Assert.Equal(expected, Matches(name.ToUpperInvariant(), rule.ToLowerInvariant()));

    [Fact]
    public void A_regex_built_to_run_away_still_answers_no_and_does_not_hang()
    {
        var name = new string('a', 60) + "!";

        // A lookahead keeps it on the backtracking engine; the pattern is a classic runaway one and must still answer no.
        Assert.False(Matches(name, "regex:^(?=.)(a|aa)+$"));
    }

    [Fact]
    public void A_regex_that_is_not_valid_is_used_as_the_literal_text_it_was_written_as()
    {
        Assert.True(Matches("Movie.(?<x-GRP.mkv", "(?<x-GRP"));
        Assert.False(Matches("Movie.1080p-GRP.mkv", "(?<x-GRP"));
    }
}
