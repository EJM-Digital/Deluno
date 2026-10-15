using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Quality;
using Deluno.Quality.Contracts;
using Deluno.Quality.Data;

namespace Deluno.Media;

/// <summary>
/// Re-decides what the library owns whenever the rules judging it change
/// (#1169): a profile is saved, a library is given another profile or media
/// plan, or one title is given a profile of its own.
///
/// <para>Wanted state is stored - "quality met" is a column, set by the import
/// that made the file - so a profile edit alone left every owned file exactly
/// as it was. Raising a cutoff from WEB 1080p to WEB 2160p changed nothing
/// until a file went missing, and the upgrade sweep, which only reads missing
/// and upgrade rows, never reached the files that should now be better. This
/// is the missing step: judge each file by what it is, under the profile that
/// now applies to it, and say so in Activity.</para>
/// </summary>
public interface IWantedStateReevaluator : IProfileChangeReevaluator
{
    /// <summary>The library was given another profile (or its media plan changed): re-decide the titles that follow it.</summary>
    Task<WantedReevaluationResult> ReevaluateLibraryAsync(string libraryId, CancellationToken cancellationToken);

    /// <summary>One title's profile changed: re-decide it in every library that holds it.</summary>
    Task<WantedReevaluationResult> ReevaluateTitleAsync(MediaKind kind, string titleId, CancellationToken cancellationToken);
}

public sealed class WantedStateReevaluator(
    ILibrariesRepository librariesRepository,
    IQualityRepository qualityRepository,
    IMediaStateRepository mediaState,
    IActivityFeedRepository activityFeedRepository) : IWantedStateReevaluator
{
    public async Task<int> ReevaluateForProfileAsync(string profileId, CancellationToken cancellationToken)
    {
        var profile = (await qualityRepository.ListQualityProfilesAsync(cancellationToken))
            .FirstOrDefault(item => string.Equals(item.Id, profileId, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
        {
            return 0;
        }

        var total = WantedReevaluationResult.None;
        var libraries = (await librariesRepository.ListLibrariesAsync(cancellationToken))
            .Where(library => string.Equals(library.QualityProfileId, profile.Id, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (var library in libraries)
        {
            total = total.Plus(await mediaState.ReevaluateWantedStateAsync(
                KindOf(library.MediaType),
                new WantedReevaluationScope(LibraryId: library.Id, OnlyTitlesWithoutOwnProfile: true),
                profile.CutoffQuality,
                profile.UpgradeUntilCutoff,
                profile.UpgradeUnknownItems,
                cancellationToken));
        }

        // Titles that chose this profile themselves, whichever library holds them.
        foreach (var kind in KindsFor(profile.MediaType))
        {
            total = total.Plus(await mediaState.ReevaluateWantedStateAsync(
                kind,
                new WantedReevaluationScope(TitleProfileId: profile.Id),
                profile.CutoffQuality,
                profile.UpgradeUntilCutoff,
                profile.UpgradeUnknownItems,
                cancellationToken));
        }

        await SayAsync($"the {profile.Name} profile changed", total, "profile", profile.Id, cancellationToken);
        return total.Titles + total.Episodes;
    }

    public async Task<int> ReevaluateForPolicySetAsync(string policySetId, CancellationToken cancellationToken)
    {
        var total = 0;
        foreach (var library in (await librariesRepository.ListLibrariesAsync(cancellationToken))
                     .Where(item => string.Equals(item.DefaultPolicySetId, policySetId, StringComparison.OrdinalIgnoreCase)))
        {
            var result = await ReevaluateLibraryAsync(library.Id, cancellationToken);
            total += result.Titles + result.Episodes;
        }

        return total;
    }

    async Task<int> IProfileChangeReevaluator.ReevaluateForLibraryAsync(string libraryId, CancellationToken cancellationToken)
    {
        var result = await ReevaluateLibraryAsync(libraryId, cancellationToken);
        return result.Titles + result.Episodes;
    }

    public async Task<WantedReevaluationResult> ReevaluateLibraryAsync(string libraryId, CancellationToken cancellationToken)
    {
        var library = (await librariesRepository.ListLibrariesAsync(cancellationToken))
            .FirstOrDefault(item => string.Equals(item.Id, libraryId, StringComparison.OrdinalIgnoreCase));
        if (library is null || string.IsNullOrWhiteSpace(library.CutoffQuality))
        {
            return WantedReevaluationResult.None;
        }

        var result = await mediaState.ReevaluateWantedStateAsync(
            KindOf(library.MediaType),
            new WantedReevaluationScope(LibraryId: library.Id, OnlyTitlesWithoutOwnProfile: true),
            library.CutoffQuality,
            library.UpgradeUntilCutoff,
            library.UpgradeUnknownItems,
            cancellationToken);
        await SayAsync($"{library.Name} now uses {library.QualityProfileName ?? "another profile"}", result, "library", library.Id, cancellationToken);
        return result;
    }

    public async Task<WantedReevaluationResult> ReevaluateTitleAsync(MediaKind kind, string titleId, CancellationToken cancellationToken)
    {
        var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
        var rows = await mediaState.ListWantedByIdsAsync(kind, [titleId], cancellationToken);
        var own = await new TitleProfileLookup(mediaState, qualityRepository).ForTitleAsync(kind, titleId, cancellationToken);
        var total = WantedReevaluationResult.None;
        foreach (var libraryId in rows.Where(row => row.Id == titleId).Select(row => row.LibraryId).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var library = libraries.FirstOrDefault(item => string.Equals(item.Id, libraryId, StringComparison.OrdinalIgnoreCase));
            if (library is null)
            {
                continue;
            }

            var judge = TitleQualityProfile.Apply(library, own);
            if (string.IsNullOrWhiteSpace(judge.CutoffQuality))
            {
                continue;
            }

            total = total.Plus(await mediaState.ReevaluateWantedStateAsync(
                kind,
                new WantedReevaluationScope(LibraryId: library.Id, TitleId: titleId),
                judge.CutoffQuality,
                judge.UpgradeUntilCutoff,
                judge.UpgradeUnknownItems,
                cancellationToken));
        }

        return total;
    }

    public async Task ReportProfileDeletedAsync(QualityProfileRemoval removal, CancellationToken cancellationToken)
    {
        var named = removal.Libraries + removal.Titles + removal.Other;
        var follows = named == 0
            ? string.Empty
            : $" {Count(removal.Libraries, "library", "libraries")}, {Count(removal.Titles, "title", "titles")} and {Count(removal.Other, "other setting", "other settings")} that named it now follow the library profile or the default one.";
        await activityFeedRepository.RecordActivityAsync(
            "quality.profile.deleted",
            $"The {removal.Name} profile was deleted.{follows}",
            null,
            null,
            "profile",
            removal.Id,
            cancellationToken);
    }

    private static string Count(int count, string one, string many) => count == 1 ? $"1 {one}" : $"{count} {many}";

    private static MediaKind KindOf(string? mediaType)
        => string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase) ? MediaKind.Series : MediaKind.Movie;

    private static IEnumerable<MediaKind> KindsFor(string? mediaType)
        => string.IsNullOrWhiteSpace(mediaType)
            ? [MediaKind.Movie, MediaKind.Series]
            : [KindOf(mediaType)];

    /// <summary>One plain line in Activity, only when the change moved something.</summary>
    private async Task SayAsync(string because, WantedReevaluationResult result, string entityType, string entityId, CancellationToken cancellationToken)
    {
        if (result.Titles + result.Episodes == 0)
        {
            return;
        }

        var moved = result.BecameUpgrade > 0
            ? $" {result.BecameUpgrade} can now be upgraded."
            : result.BecameCovered > 0
                ? $" {result.BecameCovered} now meet the quality asked for."
                : string.Empty;
        await activityFeedRepository.RecordActivityAsync(
            "library.quality.recalculated",
            $"Deluno rechecked what you own because {because}.{moved}",
            null,
            null,
            entityType,
            entityId,
            cancellationToken);
    }
}
