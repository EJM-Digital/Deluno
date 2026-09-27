using Deluno.Quality;

namespace Deluno.Integrations.Search;

public static class ProfileSearchRulesExtensions
{
    /// <summary>
    /// The request with everything its library's profile says about a search
    /// (#599): allowed tiers, upgrade-until-cutoff, size rules, acquisition
    /// rules and upgrade stop, all from one read of the profile. Every search
    /// path applies the profile through here, so none can leave part of it out.
    /// </summary>
    public static AcquisitionDecisionRequest WithProfile(this AcquisitionDecisionRequest request, ProfileSearchRules rules)
        => request with
        {
            AllowedQualities = rules.AllowedQualities,
            UpgradeUntilCutoff = rules.UpgradeUntilCutoff,
            SizeRules = rules.SizeRules,
            ProfileAcquisition = rules.Acquisition,
            UpgradeStop = rules.UpgradeStop,
            ScoreLimits = rules.ScoreLimits
        };
}
