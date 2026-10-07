using Deluno.Contracts;
using Deluno.Libraries.Contracts;
using Deluno.Quality.Contracts;
using Deluno.Quality.Data;

namespace Deluno.Media;

/// <summary>
/// Which quality profile judges one film or show (#1157).
///
/// <para>A title can carry a profile of its own, chosen on its page or when it
/// was added. Search, upgrades, the import decision and the quality card all
/// asked the title's <i>library</i> instead, so the choice was saved and then
/// ignored: a film set to a 4K profile in a 1080p library was searched for
/// 1080p. The library's profile is only the default - the title's own wins.</para>
///
/// <para>Callers keep reading a <see cref="LibraryItem"/> the way they always
/// have; <see cref="Apply"/> hands them one whose profile fields are the
/// title's, so no call site has to learn a second way to ask.</para>
/// </summary>
public static class TitleQualityProfile
{
    /// <summary>The library, but judged by <paramref name="titleProfile"/> when the title has one.</summary>
    public static LibraryItem Apply(LibraryItem library, QualityProfileItem? titleProfile)
        => titleProfile is null
            ? library
            : library with
            {
                QualityProfileId = titleProfile.Id,
                QualityProfileName = titleProfile.Name,
                CutoffQuality = titleProfile.CutoffQuality,
                UpgradeUntilCutoff = titleProfile.UpgradeUntilCutoff,
                UpgradeUnknownItems = titleProfile.UpgradeUnknownItems
            };

    public static async Task<LibraryItem> ApplyAsync(
        IMediaStateRepository mediaState,
        IQualityRepository qualityRepository,
        MediaKind kind,
        string titleId,
        LibraryItem library,
        CancellationToken cancellationToken)
        => Apply(
            library,
            await new TitleProfileLookup(mediaState, qualityRepository).ForTitleAsync(kind, titleId, cancellationToken));
}

/// <summary>
/// Looks up a title's own profile, remembering the profile list for as long as
/// the lookup lives, so a search cycle that walks a library does not read the
/// profile table once per title.
/// </summary>
public sealed class TitleProfileLookup(IMediaStateRepository mediaState, IQualityRepository qualityRepository)
{
    private IReadOnlyList<QualityProfileItem>? profiles;

    /// <summary>The title's own profile, or null when it follows its library's (or its profile no longer exists).</summary>
    public async Task<QualityProfileItem?> ForTitleAsync(MediaKind kind, string titleId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(titleId))
        {
            return null;
        }

        var profileId = await mediaState.GetTitleQualityProfileIdAsync(kind, titleId, cancellationToken);
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return null;
        }

        profiles ??= await qualityRepository.ListQualityProfilesAsync(cancellationToken);
        return profiles.FirstOrDefault(item => string.Equals(item.Id, profileId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The library as this title sees it: judged by its own profile when it has one.</summary>
    public async Task<LibraryItem> LibraryForAsync(MediaKind kind, string titleId, LibraryItem library, CancellationToken cancellationToken)
        => TitleQualityProfile.Apply(library, await ForTitleAsync(kind, titleId, cancellationToken));
}
