using Deluno.Connections.Contracts;
using Deluno.Platform.Contracts;

namespace Deluno.Persistence.Tests.Platform;

/// <summary>
/// What Deluno stores when it is told — or not told — what kind of site a
/// search source is.
///
/// This used to be normalised in two places with opposite defaults: the draft
/// endpoint answered "private" and the repository answered "public", so the
/// same request produced different values depending on which door it came
/// through. Neither was wrong enough for anyone to notice, because nothing read
/// the result. One place now, and these pin it.
/// </summary>
public sealed class IndexerPrivacyTests
{
    [Theory]
    [InlineData("private", IndexerPrivacy.Private)]
    [InlineData("PRIVATE", IndexerPrivacy.Private)]
    [InlineData("  private  ", IndexerPrivacy.Private)]
    [InlineData("public", IndexerPrivacy.Public)]
    // Prowlarr writes it camel-cased; an older Deluno hyphenated it.
    [InlineData("semiPrivate", IndexerPrivacy.SemiPrivate)]
    [InlineData("semi-private", IndexerPrivacy.SemiPrivate)]
    public void What_the_source_app_said_survives(string input, string expected)
    {
        Assert.Equal(expected, IndexerPrivacy.Normalize(input));
    }

    /// <summary>
    /// Not "public". Calling an unlabelled source open is a claim about
    /// somebody's tracker that Deluno has nothing to support, and it is exactly
    /// the claim that would be wrong in the expensive direction.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    // "usenet" is a protocol, not a privacy level. The setup guide used to send
    // it, which is how a Newznab source ended up labelled differently from a
    // Torznab one for no reason anybody could act on.
    [InlineData("usenet")]
    [InlineData("something else entirely")]
    public void Anything_it_was_not_told_is_unknown(string? input)
    {
        Assert.Equal(IndexerPrivacy.Unknown, IndexerPrivacy.Normalize(input));
    }

    /// <summary>
    /// The one question the field is asked. Both private and semi-private
    /// trackers police sharing; an open index does not, and an unknown one is
    /// not something to assume about in either direction.
    /// </summary>
    [Theory]
    [InlineData("private", true)]
    [InlineData("semiPrivate", true)]
    [InlineData("public", false)]
    [InlineData(null, false)]
    public void Only_a_site_that_polices_sharing_expects_it(string? input, bool expected)
    {
        Assert.Equal(expected, IndexerPrivacy.ExpectsSharing(input));
    }

    /// <summary>
    /// The web app hardcodes these in <c>STRICT_SHARING</c>
    /// (apps/web/src/routes/connections/forms.ts) because it cannot import C#,
    /// the same way it mirrors the default rule in its settings snapshot. This
    /// is the backend half of that pair; its web unit test is the other.
    /// </summary>
    [Fact]
    public void The_strict_rule_is_what_both_sides_agree_it_is()
    {
        Assert.Equal(SharingPolicy.ModeShareThenTidy, SharingPolicy.Strict.Mode);
        Assert.Equal(336, SharingPolicy.Strict.ForHours);
        Assert.Equal(1.0, SharingPolicy.Strict.UntilRatio);
        Assert.Equal(14, SharingPolicy.Strict.StuckAfterDays);
        // Never gives up on its own: on a site that polices hit-and-runs,
        // reclaiming space is not worth an account.
        Assert.Equal(SharingPolicy.StuckKeepWaiting, SharingPolicy.Strict.StuckAction);
    }
}
