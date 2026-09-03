using Deluno.Quality.Contracts;
using Deluno.Quality.Guides;

namespace Deluno.Quality.ReleasePreferences;

/// <summary>
/// A profile that has not been saved, and one release name to judge against it.
///
/// <para><b>Why this exists separately from the preview endpoint.</b> That one
/// takes a persisted <c>planId</c>, which is right for asking "what would my
/// live profile do with this release" and useless for #386's actual promise:
/// change the sound answer and watch a release flip while you are still
/// deciding. Nothing has been saved yet at that moment, so there is no plan id
/// to name.</para>
///
/// <para>Nothing is written. The plan is compiled in memory, used once, and
/// dropped — a half-answered profile must not leave a plan row behind, and an
/// owner who abandons a step must not have to clean anything up.</para>
/// </summary>
public sealed record DraftProfileJudgementRequest(
    string? Name,
    string? MediaType,
    IReadOnlyList<string>? AllowedQualities,
    string? CutoffQuality,
    IReadOnlyList<string>? CustomFormatIds,
    bool UpgradeUntilCutoff,
    bool UpgradeUnknownItems,
    bool AllowLowerQualityReplacements,
    string? ReleaseName,
    string? CurrentReleaseName = null,
    string? CandidateQuality = null,
    string? CurrentQuality = null,
    int? Seeders = null);

/// <summary>
/// What the draft profile makes of that release, and why — in the same typed
/// shape the saved-plan preview returns, so a step and the profile's own plan
/// panel cannot disagree about what a verdict looks like.
/// </summary>
public sealed record DraftProfileJudgement(
    string ReleaseName,
    ReleasePreferencePlan Plan,
    IReadOnlyList<PreferenceFact> CandidateFacts,
    PreferenceEvaluation CandidateEvaluation,
    string? CurrentReleaseName,
    PreferenceEvaluation? CurrentEvaluation,
    PreferenceComparison? Comparison,
    IReadOnlyList<string> Warnings,
    bool RequiresReview,
    /// <summary>
    /// Why the profile's allowed list refuses this release outright, or null
    /// when it does not. Separate from the evaluation because it is a gate
    /// rather than a preference, and the two must not be reported as though
    /// one could outrank the other.
    /// </summary>
    string? Refusal = null);

public static class DraftProfileJudge
{
    /// <summary>
    /// The transient id a compiled draft carries.
    ///
    /// <para>Fixed rather than generated. A draft is never persisted and never
    /// compared against another draft, so a fresh id per keystroke would be
    /// noise in every log line and in the evaluation's own <c>PlanId</c> — and
    /// a reader seeing this value knows immediately that nothing was saved.</para>
    /// </summary>
    public const string DraftPlanId = "draft";

    public static DraftProfileJudgement Judge(
        DraftProfileJudgementRequest request,
        IReadOnlyList<CustomFormatItem> customFormats,
        GuidePackage? guidePackage,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(request);

        var now = timeProvider.GetUtcNow();
        var profile = new QualityProfileItem(
            Id: DraftPlanId,
            Name: string.IsNullOrWhiteSpace(request.Name) ? "Draft profile" : request.Name.Trim(),
            MediaType: string.IsNullOrWhiteSpace(request.MediaType) ? "movies" : request.MediaType.Trim(),
            CutoffQuality: request.CutoffQuality?.Trim() ?? string.Empty,
            AllowedQualities: string.Join(',', request.AllowedQualities ?? []),
            CustomFormatIds: string.Join(',', request.CustomFormatIds ?? []),
            UpgradeUntilCutoff: request.UpgradeUntilCutoff,
            UpgradeUnknownItems: request.UpgradeUnknownItems,
            AllowLowerQualityReplacements: request.AllowLowerQualityReplacements,
            PresetId: null,
            PresetVersion: null,
            PresetDrifted: false,
            CreatedUtc: now,
            UpdatedUtc: now);

        var compilation = ReleasePreferencePlanFactory.CompileProfile(profile, customFormats, guidePackage);
        var plan = compilation.Plan;

        var candidateFacts = ReleasePreferenceFactFactory.WithTransientSignals(
            plan,
            ReleasePreferenceFactFactory.FromReleaseName(
                plan, request.ReleaseName, request.CandidateQuality, "draft-judgement"),
            request.Seeders);
        var candidateEvaluation = ReleasePreferenceEvaluator.Evaluate(plan, candidateFacts);

        // The allowed list is a gate the preference plan deliberately does not
        // enforce: its family has to be able to place a held file better than
        // every allowed tier, or a profile allowing up to Bluray 1080p would
        // ask to downgrade your Bluray 2160p. That is right for ranking and
        // wrong for grabbing, so the gate is asked here - the same question
        // ReleaseDecisionEngine asks of every candidate during a real search.
        var candidateQuality = MediaPolicyCatalog.Current.NormalizeQuality(request.CandidateQuality)
            ?? MediaPolicyCatalog.Current.DetectQuality(request.ReleaseName);
        var refusal = AllowedQualityGate.Accepts(request.AllowedQualities, candidateQuality)
            ? null
            : AllowedQualityGate.Refusal(request.AllowedQualities ?? [], candidateQuality ?? "That quality");

        // The "is this better than what I have" half is optional, because most
        // of the time the question being asked at a step is simply "would this
        // be accepted at all".
        PreferenceEvaluation? currentEvaluation = null;
        PreferenceComparison? comparison = null;
        if (!string.IsNullOrWhiteSpace(request.CurrentReleaseName) || !string.IsNullOrWhiteSpace(request.CurrentQuality))
        {
            var currentFacts = ReleasePreferenceFactFactory.FromReleaseName(
                plan, request.CurrentReleaseName, request.CurrentQuality, "draft-judgement-current");
            currentEvaluation = ReleasePreferenceEvaluator.Evaluate(plan, currentFacts);
            comparison = ReleasePreferenceEvaluator.Compare(plan, currentFacts, candidateFacts);
        }

        return new DraftProfileJudgement(
            request.ReleaseName?.Trim() ?? string.Empty,
            plan,
            candidateFacts,
            candidateEvaluation,
            request.CurrentReleaseName?.Trim(),
            currentEvaluation,
            comparison,
            compilation.Warnings,
            compilation.RequiresReview,
            refusal);
    }
}
