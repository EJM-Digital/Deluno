using Deluno.Integrations.Search;
using Deluno.Quality;

namespace Deluno.Integrations.Tests.Search;

/// <summary>
/// #630: Size Rules judged every release against a fixed runtime - two hours
/// for a film, 45 minutes for an episode (#600) - so Tears of Steel (2012), a
/// 12-minute short, had every real 1080p release rejected: "Size 0.2 GB is
/// below the 1.5 GB minimum configured for WEB 1080p", a floor sized for a
/// film ten times its length.
///
/// <para><b>The fix, the way Radarr/Sonarr do it.</b> The configured band is
/// read as a rate against the reference runtime the fixed assumption used,
/// and scaled against the title's real runtime when metadata supplies one.
/// With no runtime known there is nothing to scale by, so the fixed
/// assumption remains the only fallback.</para>
/// </summary>
public sealed class ASizeFloorScalesWithRuntimeTests
{
    private static readonly IReadOnlyList<ProfileSizeRule> WebFilmRules =
        [new ProfileSizeRule("WEB 1080p", 1.5, 25.0, 420, 3800)];

    [Fact]
    public void A_twelve_minute_short_passes_a_release_that_used_to_be_rejected_on_size()
    {
        // Tears of Steel (2012), Blender Foundation, 12 minutes. A real
        // 1080p release of it is genuinely around 0.2 GB, and used to be
        // rejected against a floor sized for a two-hour film.
        var decision = Decide(sizeBytes: 200_000_000, runtimeMinutes: 12);

        Assert.Equal("preferred", decision.Status);
        // #771: within range now also scores a small bonus for how close the
        // size is to the tier's Preferred value, so this is no longer always
        // exactly 80 - only ever at or above it.
        Assert.True(decision.SizeScore >= 80);
        Assert.DoesNotContain(decision.RiskFlags, flag =>
            flag.Contains("below", StringComparison.OrdinalIgnoreCase) &&
            flag.Contains("minimum", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(decision.Reasons, reason => reason == "Size 0.2 GB is within the expected range for WEB 1080p.");
    }

    [Fact]
    public void A_normal_length_film_with_no_known_runtime_still_uses_the_two_hour_fallback()
    {
        // The exact same release, but nothing supplied a runtime: still
        // judged against the fixed two-hour assumption, exactly as before
        // #630, and still rejected exactly as the live report described.
        var decision = Decide(sizeBytes: 200_000_000, runtimeMinutes: null);

        Assert.Equal(ReleaseDecisionStatuses.Rejected, decision.Status);
        Assert.Equal(-180, decision.SizeScore);
        Assert.Contains(decision.RiskFlags, flag =>
            flag == "Size 0.2 GB is below the 1.5 GB minimum configured for WEB 1080p for a 2-hour runtime.");
    }

    [Fact]
    public void The_rejection_message_names_the_runtime_it_used()
    {
        // Still below its own scaled floor even at 12 minutes (roughly 0.15
        // GB), so this one is rejected too - and should say the floor was
        // judged against the title's real 12-minute runtime.
        var knownRuntime = Decide(sizeBytes: 100_000_000, runtimeMinutes: 12);
        Assert.Equal(ReleaseDecisionStatuses.Rejected, knownRuntime.Status);
        Assert.Contains(knownRuntime.RiskFlags, flag => flag.Contains("for a 12-minute runtime", StringComparison.Ordinal));
        Assert.DoesNotContain(knownRuntime.RiskFlags, flag => flag.Contains("for a 2-hour runtime", StringComparison.Ordinal));

        // Same undersized release with no runtime known: the fallback names
        // itself instead, so a person can tell why a different threshold
        // applied.
        var unknownRuntime = Decide(sizeBytes: 100_000_000, runtimeMinutes: null);
        Assert.Equal(ReleaseDecisionStatuses.Rejected, unknownRuntime.Status);
        Assert.Contains(unknownRuntime.RiskFlags, flag => flag.Contains("for a 2-hour runtime", StringComparison.Ordinal));
    }

    private static ReleaseDecision Decide(long sizeBytes, int? runtimeMinutes)
        => ReleaseDecisionEngine.Decide(new ReleaseDecisionInput(
            ReleaseName: "Tears.of.Steel.2012.1080p.WEB-DL.DD5.1.H.264-GRP",
            Quality: "WEB 1080p",
            CurrentQuality: null,
            TargetQuality: "WEB 1080p",
            SizeBytes: sizeBytes,
            Seeders: 20,
            DownloadUrl: "https://example.com/release",
            SourcePriorityScore: 100,
            CustomFormatScore: 0,
            SizeRules: WebFilmRules,
            MediaType: "movies",
            RuntimeMinutes: runtimeMinutes));
}
