using System.Text.Json;
using Deluno.Jobs.Data;
using Deluno.Quality.Data;
using Deluno.Quality.Guides;

namespace Deluno.Platform.Migration;

public interface IGuideRuleMigrationService
{
    Task<GuideRuleMigrationPreview> PreviewAsync(CancellationToken cancellationToken);

    Task<GuideRuleMigrationResult> ApplyAsync(
        GuideRuleMigrationApplyRequest request,
        CancellationToken cancellationToken);
}

public sealed record GuideRuleMigrationApplyRequest(
    string PreviewHash,
    IReadOnlyList<string> RuleIds);

public sealed record GuideRuleMigrationResult(
    IReadOnlyList<GuideRuleMigrationOutcome> Outcomes,
    int Moved,
    int Skipped);

public sealed record GuideRuleMigrationOutcome(
    string RuleId,
    string Name,
    bool Moved,
    string Message);

/// <summary>
/// Moves rules made from Deluno's old guide catalogue onto genuine TRaSH
/// formats (or Deluno's own extras), only for the rules the owner chose from a
/// preview that still describes the library, and writes each move to Activity
/// with what the rule was and what it became.
/// </summary>
public sealed class GuideRuleMigrationService(
    IQualityRepository qualityRepository,
    IGuidePackageStore guidePackageStore,
    IActivityFeedRepository activityFeedRepository) : IGuideRuleMigrationService
{
    public const string ActivityCategory = "quality.custom-format.guide-moved";

    public async Task<GuideRuleMigrationPreview> PreviewAsync(CancellationToken cancellationToken)
    {
        var rules = await qualityRepository.ListCustomFormatsAsync(cancellationToken);
        var profiles = await qualityRepository.ListQualityProfilesAsync(cancellationToken);
        var package = (await guidePackageStore.GetCurrentAsync(cancellationToken)).Package;
        return GuideRuleMigrationPlanner.Plan(rules, profiles, package);
    }

    public async Task<GuideRuleMigrationResult> ApplyAsync(
        GuideRuleMigrationApplyRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var preview = await PreviewAsync(cancellationToken);
        if (!string.Equals(preview.PreviewHash, request.PreviewHash?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Your rules or the guide changed after this preview. Review the move again before applying it.");
        }

        var chosen = (request.RuleIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var outcomes = new List<GuideRuleMigrationOutcome>();
        foreach (var item in preview.Items.Where(item => chosen.Contains(item.RuleId)))
        {
            var moved = await qualityRepository.ReplaceCustomFormatDefinitionAsync(
                item.RuleId,
                item.NewName,
                item.NewTrashId,
                item.NewConditions,
                item.CurrentConditions,
                cancellationToken);
            if (moved is null)
            {
                outcomes.Add(new GuideRuleMigrationOutcome(
                    item.RuleId,
                    item.CurrentName,
                    false,
                    "Not moved: the rule was removed or edited after the preview."));
                continue;
            }

            var message = item.Action switch
            {
                GuideRuleMigrationPlanner.AdoptTrashFormat =>
                    $"Moved the {MediaLabel(item.MediaType)} rule “{item.CurrentName}” onto TRaSH's “{item.NewFormatName}” format.",
                GuideRuleMigrationPlanner.AdoptDelunoFormat =>
                    $"Moved the {MediaLabel(item.MediaType)} rule “{item.CurrentName}” onto Deluno's own “{item.NewFormatName}” format.",
                _ =>
                    $"Kept the {MediaLabel(item.MediaType)} rule “{item.CurrentName}” as your own rule; TRaSH has no version of it."
            };
            await activityFeedRepository.RecordActivityAsync(
                ActivityCategory,
                message,
                JsonSerializer.Serialize(new
                {
                    action = item.Action,
                    guidePackageVersion = preview.PackageVersion,
                    before = new { name = item.CurrentName, trashId = item.CurrentTrashId, conditions = item.CurrentConditions },
                    after = new { name = moved.Name, trashId = moved.TrashId, conditions = moved.Conditions },
                    score = moved.Score,
                    profiles = item.ProfileNames
                }),
                null,
                "custom-format",
                item.RuleId,
                cancellationToken);
            outcomes.Add(new GuideRuleMigrationOutcome(item.RuleId, moved.Name, true, message));
        }

        return new GuideRuleMigrationResult(
            outcomes,
            outcomes.Count(outcome => outcome.Moved),
            outcomes.Count(outcome => !outcome.Moved));
    }

    private static string MediaLabel(string mediaType)
        => string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase) ? "TV" : "movies";
}
