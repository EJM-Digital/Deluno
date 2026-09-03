using Deluno.Libraries.Contracts;
using Deluno.Platform.Contracts;
using Deluno.Quality.Contracts;
using Deluno.Quality.ReleasePreferences;
using Deluno.Quality;

namespace Deluno.Integrations.Search;

public interface IMediaSearchPlanner
{
    Task<MediaSearchPlan> BuildPlanAsync(
        string title,
        int? year,
        string mediaType,
        string? currentQuality,
        string? targetQuality,
        IReadOnlyList<LibrarySourceLinkItem> sources,
        IReadOnlyList<CustomFormatItem>? customFormats = null,
        int? seasonNumber = null,
        int? episodeNumber = null,
        /// <summary>
        /// The quality tiers the governing profile permits. Null or empty leaves
        /// tier selection to the cutoff alone; a non-empty list rejects anything
        /// outside it.
        /// </summary>
        IReadOnlyList<string>? allowedQualities = null,
        CancellationToken cancellationToken = default,
        IReadOnlyList<string>? tagNames = null,
        string searchKind = AcquisitionSearchKinds.Automatic,
        DateTimeOffset? availableUtc = null,
        int? currentCustomFormatScore = null,
        string? currentReleaseName = null,
        bool upgradeUntilCutoff = true,
        string? numberingScheme = null,
        int? absoluteNumber = null,
        DateOnly? airDate = null,
        int? sceneSeasonNumber = null,
        int? sceneEpisodeNumber = null,
        PreferenceEvaluationSnapshot? currentPreferenceEvaluation = null,
        ReleasePreferencePlan? preferencePlan = null,
        bool currentFilePresent = false,
        IReadOnlyList<ProfileSizeRule>? sizeRules = null,
        QualityUpgradeStopPolicy? upgradeStop = null);
}
