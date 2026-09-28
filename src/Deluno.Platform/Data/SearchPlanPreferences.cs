namespace Deluno.Platform.Data;

/// <summary>
/// #974: the one call every search path uses to read the current Proper/Repack
/// acquisition choice ("Do not upgrade automatically") before building a
/// release-preference plan.
///
/// <para>Before this, only the manual grab (<c>MediaGrabHandler</c>) read
/// <see cref="Deluno.Platform.Contracts.PlatformSettingsSnapshot.ProperRepackAcquisitionMode"/>
/// and passed it into <c>QualityProfileResolver.ResolveReleasePreferencePlanAsync</c>.
/// Every automatic search path built its plan without it, so the setting was
/// honoured only by a manual grab: an automatic search still grabbed a Proper
/// or Repack of a file already installed. Every caller that builds a plan for
/// a search now reads the mode through here, so a second copy of "read the
/// settings, pull out the field" cannot silently drift and leave a fifth path
/// behind.</para>
/// </summary>
public static class SearchPlanPreferences
{
    public static async Task<string> ResolveProperRepackAcquisitionModeAsync(
        IPlatformSettingsRepository platformSettingsRepository,
        CancellationToken cancellationToken)
    {
        var settings = await platformSettingsRepository.GetAsync(cancellationToken);
        return settings.ProperRepackAcquisitionMode;
    }
}
