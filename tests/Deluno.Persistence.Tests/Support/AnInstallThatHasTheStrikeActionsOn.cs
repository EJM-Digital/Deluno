using Deluno.Platform.Contracts;
using Deluno.Platform.Data;

namespace Deluno.Persistence.Tests.Support;

/// <summary>
/// #1194: a brand-new install has no strike rule or acting switch on. A test that is about what happens once
/// strikes run out is about an install whose person turned those on (or one that already existed and kept them),
/// so it says so here instead of leaning on a default.
/// </summary>
internal static class AnInstallThatHasTheStrikeActionsOn
{
    public static async Task ApplyAsync(IPlatformSettingsRepository settings)
        => await settings.SaveAsync(
            PlatformSettingsPatchMerger.Apply(
                await settings.GetAsync(CancellationToken.None),
                new PatchPlatformSettingsRequest(
                    CleanupBlockReleaseAfterThreshold: true,
                    CleanupQueueReplacementAfterThreshold: true,
                    DownloadHealthNoProgressEnabled: true)),
            CancellationToken.None);
}
