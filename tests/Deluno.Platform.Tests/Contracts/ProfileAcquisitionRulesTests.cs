using Deluno.Quality;

namespace Deluno.Platform.Tests.Contracts;

/// <summary>
/// A profile's own acquisition answers.
///
/// <para>#394: these were keyed by tag, so protocol preference and delays sat
/// apart from the seven answers they belong beside — and a profile could not
/// want usenet for anime and torrents for films without inventing a tag to say
/// so.</para>
/// </summary>
public sealed class ProfileAcquisitionRulesTests
{
    [Fact]
    public void Two_profiles_can_want_different_ways_of_fetching()
    {
        var anime = new ProfileAcquisitionRules(PreferredProtocol: "usenet").Normalize();
        var films = new ProfileAcquisitionRules(PreferredProtocol: "torrent").Normalize();

        Assert.Equal("usenet", anime.PreferredProtocol);
        Assert.Equal("torrent", films.PreferredProtocol);
        Assert.False(anime.IsEmpty);
    }

    [Fact]
    public void Having_no_opinion_is_the_starting_state_and_stores_nothing()
    {
        var none = new ProfileAcquisitionRules();

        Assert.True(none.IsEmpty);
        // Nothing stored means nothing to read back, which is what every
        // profile had before it could hold an answer at all.
        Assert.Equal(string.Empty, ProfileAcquisitionRulesCodec.Serialize(none));
        Assert.Null(ProfileAcquisitionRulesCodec.Deserialize(string.Empty));
    }

    [Fact]
    public void A_protocol_nobody_recognises_becomes_no_preference()
    {
        // Never a refusal. Reading "sideways" as a protocol Deluno must match
        // would silently empty the results for a profile nobody had touched.
        Assert.Equal("any", new ProfileAcquisitionRules(PreferredProtocol: "sideways").Normalize().PreferredProtocol);
        Assert.Equal("any", new ProfileAcquisitionRules(PreferredProtocol: "").Normalize().PreferredProtocol);
    }

    [Fact]
    public void A_negative_delay_is_read_as_no_delay()
    {
        // "Fetch it before it exists" is not an answer anybody meant to give.
        var rules = new ProfileAcquisitionRules(UsenetDelayMinutes: -30, TorrentDelayMinutes: -1).Normalize();

        Assert.Equal(0, rules.UsenetDelayMinutes);
        Assert.Equal(0, rules.TorrentDelayMinutes);
    }

    [Fact]
    public void Terms_are_tidied_so_one_intent_typed_two_ways_is_stored_one_way()
    {
        var rules = new ProfileAcquisitionRules(MustContain: " FLUX , ntb,NTb ,, flux ").Normalize();

        Assert.Equal("FLUX, ntb", rules.MustContain);
    }

    [Fact]
    public void An_unreadable_answer_leaves_the_profile_with_no_acquisition_opinion()
    {
        // Rather than a must-contain nobody can see refusing every release.
        Assert.Null(ProfileAcquisitionRulesCodec.Deserialize("{not json"));

        var rules = new ProfileAcquisitionRules(PreferredProtocol: "usenet", MustNotContain: "HDTV");
        Assert.Equal(rules.Normalize(), ProfileAcquisitionRulesCodec.Deserialize(ProfileAcquisitionRulesCodec.Serialize(rules)));
    }
}
