using Deluno.Contracts;

namespace Deluno.Persistence.Tests.Media;

public sealed class SubtitleTimingPolicyTests
{
    [Fact]
    public void The_default_policy_repairs_below_the_file_specific_rung()
    {
        var policy = new SubtitleTimingPolicy();

        Assert.True(policy.ShouldSync(SubtitleMatch.AnyRelease));
        Assert.True(policy.ShouldSync(SubtitleMatch.SameSource));
        Assert.False(policy.ShouldSync(SubtitleMatch.MadeForThisFile));
    }

    [Fact]
    public void A_library_can_limit_repair_to_subtitles_with_no_source_match()
    {
        var policy = new SubtitleTimingPolicy(SyncOnlyBelow: SubtitleSyncThreshold.SameSource);

        Assert.True(policy.ShouldSync(SubtitleMatch.AnyRelease));
        Assert.False(policy.ShouldSync(SubtitleMatch.SameSource));
        Assert.False(policy.ShouldSync(SubtitleMatch.MadeForThisFile));
    }

    [Fact]
    public void Disabled_repair_never_qualifies_a_subtitle()
    {
        var policy = new SubtitleTimingPolicy(Enabled: false);

        Assert.False(policy.ShouldSync(SubtitleMatch.AnyRelease));
        Assert.False(policy.ShouldSync(SubtitleMatch.SameSource));
    }

    [Fact]
    public void Policy_normalization_bounds_repair_and_canonicalizes_provider_names()
    {
        var normalized = SubtitleTimingPolicyCodec.Normalize(new SubtitleTimingPolicy(
            SyncOnlyBelow: " SAME-SOURCE ",
            MaxOffsetSeconds: 999,
            RequiredPeakSigma: 99,
            ExcludedProviders: [" ProviderB ", "providerA", "PROVIDERA", " "]));

        Assert.NotNull(normalized);
        Assert.Equal(SubtitleSyncThreshold.SameSource, normalized!.SyncOnlyBelow);
        Assert.Equal(300, normalized.MaxOffsetSeconds);
        Assert.Equal(10, normalized.RequiredPeakSigma);
        Assert.Equal(["providera", "providerb"], normalized.ExcludedProviders);
    }

    [Fact]
    public void An_empty_policy_is_preserved_as_disabled_only_when_explicitly_disabled()
    {
        Assert.Null(SubtitleTimingPolicyCodec.Normalize(null));

        var disabled = SubtitleTimingPolicyCodec.Normalize(new SubtitleTimingPolicy(Enabled: false));

        Assert.NotNull(disabled);
        Assert.False(disabled!.Enabled);
    }
}
