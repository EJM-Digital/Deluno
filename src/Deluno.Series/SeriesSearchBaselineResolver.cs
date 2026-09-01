using Deluno.Contracts;
using Deluno.Integrations.Search;
using Deluno.Jobs.Contracts;
using Deluno.Media;
using Deluno.Quality.ReleasePreferences;
using Deluno.Series.Data;

namespace Deluno.Series;

/// <summary>
/// Resolves replacement evidence at episode scope. A series-level wanted row
/// cannot stand in for one installed episode because its quality, path, size
/// and immutable-plan evaluation may belong to a different file.
/// </summary>
public static class SeriesSearchBaselineResolver
{
    public static async Task<EpisodeSearchBaseline> ResolveEpisodeAsync(
        ISeriesCatalogRepository seriesRepository,
        IMediaStateRepository mediaStateRepository,
        string seriesId,
        string episodeId,
        string libraryId,
        CancellationToken cancellationToken)
    {
        var filePath = await seriesRepository.GetEpisodeFilePathAsync(episodeId, cancellationToken);
        var fileSizeBytes = await seriesRepository.GetEpisodeFileSizeBytesAsync(episodeId, cancellationToken);
        var currentQuality = await seriesRepository.GetEpisodeCurrentQualityAsync(episodeId, cancellationToken);
        var targetQuality = await seriesRepository.GetEpisodeTargetQualityAsync(
            episodeId,
            libraryId,
            cancellationToken);
        var preferenceEvaluation = string.IsNullOrWhiteSpace(filePath)
            ? null
            : await mediaStateRepository.GetLatestPreferenceEvaluationSnapshotAsync(
                MediaKind.Series,
                seriesId,
                libraryId,
                fileIdentity: null,
                cancellationToken,
                filePath,
                fileSizeBytes);

        return new EpisodeSearchBaseline(
            filePath,
            fileSizeBytes,
            currentQuality,
            targetQuality,
            preferenceEvaluation);
    }

    public static async Task<IReadOnlyList<string>> ListInstalledEpisodeIdsAsync(
        ISeriesCatalogRepository seriesRepository,
        IReadOnlyList<string> episodeIds,
        CancellationToken cancellationToken)
    {
        var installed = new List<string>();
        foreach (var episodeId in episodeIds)
        {
            if (!string.IsNullOrWhiteSpace(await seriesRepository.GetEpisodeFilePathAsync(
                    episodeId,
                    cancellationToken)))
            {
                installed.Add(episodeId);
            }
        }

        return installed;
    }

    public static SeasonPackReplacementDecision EvaluateSeasonPackCandidate(
        ReleasePreferencePlan plan,
        MediaSearchCandidate candidate,
        IReadOnlyList<SeasonPackInstalledEpisode> installedEpisodes)
    {
        if (installedEpisodes.Count == 0)
        {
            return new SeasonPackReplacementDecision(true, [], [], "The season has no installed episode files to replace.");
        }

        var invalid = installedEpisodes.FirstOrDefault(item =>
            string.IsNullOrWhiteSpace(item.FilePath) ||
            item.PreferenceEvaluation is null ||
            !string.Equals(item.PreferenceEvaluation.PlanId, plan.Id, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(item.PreferenceEvaluation.PlanVersion, plan.Version, StringComparison.Ordinal) ||
            !string.Equals(item.PreferenceEvaluation.PlanHash, plan.PlanHash, StringComparison.OrdinalIgnoreCase));
        if (invalid is not null)
        {
            return new SeasonPackReplacementDecision(
                false,
                [],
                [],
                $"Episode '{invalid.EpisodeId}' does not have an exact installed-file evaluation for the current plan.");
        }

        var candidateFacts = ReleasePreferenceFactFactory.WithTransientSignals(
            plan,
            ReleasePreferenceFactFactory.FromReleaseName(plan, candidate.ReleaseName, candidate.Quality),
            candidate.Seeders);
        var comparisons = installedEpisodes
            .Select(item => new SeasonPackEpisodeComparison(
                item.EpisodeId,
                ReleasePreferenceEvaluator.Compare(plan, item.PreferenceEvaluation!.Facts, candidateFacts)))
            .ToArray();
        var notUpgrades = comparisons
            .Where(item => item.Comparison.Status != PreferenceCandidateStatus.Upgrade)
            .ToArray();
        if (notUpgrades.Length > 0)
        {
            return new SeasonPackReplacementDecision(
                false,
                comparisons,
                [],
                $"The pack candidate is not a proven upgrade for {notUpgrades.Length} installed episode file(s).");
        }

        var targets = installedEpisodes
            .Select(item => new DispatchReplacementTarget(item.EpisodeId, item.FilePath))
            .OrderBy(item => item.EntityId, StringComparer.Ordinal)
            .ToArray();
        return new SeasonPackReplacementDecision(
            true,
            comparisons,
            targets,
            $"The candidate is a proven same-plan upgrade for all {installedEpisodes.Count} installed episode file(s).");
    }
}

public sealed record EpisodeSearchBaseline(
    string? FilePath,
    long? FileSizeBytes,
    string? CurrentQuality,
    string? TargetQuality,
    PreferenceEvaluationSnapshot? PreferenceEvaluation);

public sealed record SeasonPackInstalledEpisode(
    string EpisodeId,
    string FilePath,
    PreferenceEvaluationSnapshot? PreferenceEvaluation);

public sealed record SeasonPackEpisodeComparison(
    string EpisodeId,
    PreferenceComparison Comparison);

public sealed record SeasonPackReplacementDecision(
    bool Authorized,
    IReadOnlyList<SeasonPackEpisodeComparison> Comparisons,
    IReadOnlyList<DispatchReplacementTarget> Targets,
    string Reason);
