using Deluno.Contracts;
using Deluno.Integrations.Search;

namespace Deluno.Integrations.Tests.Search;

/// <summary>
/// The "Never grab" words on Release Preferences &gt; Safeguards: a release whose name carries any of them, in any letter case,
/// is refused outright with the word in the reason, and a release that carries none is not refused for them.
/// </summary>
public sealed class NeverGrabWordsRefuseAReleaseTests
{
    private const string Clean = "The.Kid.1921.1080p.BluRay.x264-GRP";

    private static ReleaseDecision Decide(string name, params string[] neverGrab)
        => ReleaseDecisionEngine.Decide(new ReleaseDecisionInput(
            ReleaseName: name,
            Quality: "Bluray 1080p",
            CurrentQuality: null,
            TargetQuality: "Bluray 1080p",
            SizeBytes: 4L * 1024 * 1024 * 1024,
            Seeders: 10,
            DownloadUrl: "https://tracker.example/t/1",
            SourcePriorityScore: 100,
            CustomFormatScore: 0,
            NeverGrabPatterns: neverGrab,
            IndexerProtocol: "tracker",
            MediaType: "movies"));

    [Theory]
    [InlineData("x264")]
    [InlineData("X264")]
    [InlineData("grp")]
    [InlineData("BluRay.x264")]
    public void A_release_whose_name_carries_a_never_grab_word_is_refused_whatever_the_letter_case(string word)
    {
        var decision = Decide(Clean, word);

        Assert.Equal(ReleaseDecisionStatuses.Rejected, decision.Status);
        Assert.Contains(decision.Reasons, reason => reason.Contains($"never-grab pattern '{word}'", StringComparison.Ordinal));
    }

    [Fact]
    public void A_release_that_carries_none_of_the_words_is_not_refused_for_them_and_a_blank_entry_matches_nothing()
    {
        var decision = Decide(Clean, "CAMRip", "NOGROUP", "   ", "");

        Assert.DoesNotContain(decision.Reasons, reason => reason.Contains("never-grab", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_word_is_plain_text_so_regex_characters_in_it_are_not_special()
    {
        var plain = Decide("The.Kid.1921.1080p.BluRay.x264-GRP", "x2.4");
        var literal = Decide("The.Kid.1921.1080p.BluRay.x2.4-GRP", "x2.4");

        Assert.DoesNotContain(plain.Reasons, reason => reason.Contains("never-grab", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(literal.Reasons, reason => reason.Contains("never-grab pattern 'x2.4'", StringComparison.Ordinal));
    }
}
