using Deluno.Quality.Contracts;
using Deluno.Quality.ReleasePreferences;

namespace Deluno.Platform.Tests.Contracts;

/// <summary>
/// Judging a profile nobody has saved.
///
/// <para>#386's promise is that you change an answer and watch a real release
/// flip while you are still deciding. Everything else in the release-preference
/// surface works from a persisted plan id, which is exactly what a half-answered
/// profile does not have — so this is the piece that makes the promise
/// possible, and these are the two things it has to get right: the same verdict
/// a saved plan would give, and no row left behind.</para>
/// </summary>
public sealed class DraftProfileJudgeTests
{
    private static readonly TimeProvider Clock = TimeProvider.System;

    [Fact]
    public void An_answer_that_forbids_a_trait_turns_a_release_carrying_it_down()
    {
        var allowed = new[] { "Bluray-1080p", "WEB-1080p" };

        var accepted = Judge(allowed, "WEB-1080p", "Dune.2021.1080p.BluRay.x264-NTb");
        Assert.True(accepted.CandidateEvaluation.HardGatesPassed);

        // A release below the allowed ladder is the same question asked of the
        // quality family, and it is the one every step ultimately qualifies.
        var refused = Judge(allowed, "WEB-1080p", "Dune.2021.480p.DVDRip.x264-NTb");
        Assert.NotEqual(
            accepted.CandidateEvaluation.Status,
            refused.CandidateEvaluation.Status);
    }

    [Fact]
    public void Changing_where_to_stop_changes_the_verdict_for_the_same_release()
    {
        const string release = "Dune.2021.1080p.WEB-DL.x264-NTb";

        // Step 1 is "how good, and when to stop". Stopping here means this
        // release is the answer; stopping higher means it is a step on the way.
        var stopsHere = Judge(["WEB-1080p"], "WEB-1080p", release);
        var wantsBetter = Judge(["Bluray-2160p", "WEB-1080p"], "Bluray-2160p", release);

        // This is the whole point of the endpoint: the release did not move,
        // the answer did, and the judgement followed the answer.
        Assert.Equal(PreferenceEvaluationStatus.MeetsPlan, stopsHere.CandidateEvaluation.Status);
        Assert.Equal(PreferenceEvaluationStatus.BelowGoal, wantsBetter.CandidateEvaluation.Status);
    }

    [Fact]
    public void A_draft_is_never_mistaken_for_a_saved_plan()
    {
        var judgement = Judge(["WEB-1080p"], "WEB-1080p", "Dune.2021.1080p.WEB-DL.x264-NTb");

        // Anything reading a log line, an evaluation or the returned plan can
        // tell at a glance that nothing was persisted. The compiler namespaces
        // the id, so the check is on the tail rather than the whole string.
        Assert.EndsWith(DraftProfileJudge.DraftPlanId, judgement.Plan.Id, StringComparison.Ordinal);
        Assert.EndsWith(DraftProfileJudge.DraftPlanId, judgement.CandidateEvaluation.PlanId, StringComparison.Ordinal);
        Assert.Equal(judgement.Plan.Id, judgement.CandidateEvaluation.PlanId);
    }

    [Fact]
    public void Comparing_against_a_held_file_is_offered_only_when_one_is_named()
    {
        var alone = Judge(["Bluray-2160p", "WEB-1080p"], "Bluray-2160p", "Dune.2021.2160p.BluRay.x265-NTb");
        Assert.Null(alone.CurrentEvaluation);
        Assert.Null(alone.Comparison);

        var against = DraftProfileJudge.Judge(
            Request(["Bluray-2160p", "WEB-1080p"], "Bluray-2160p", "Dune.2021.2160p.BluRay.x265-NTb")
                with { CurrentReleaseName = "Dune.2021.1080p.WEB-DL.x264-NTb" },
            [],
            null,
            Clock);

        Assert.NotNull(against.CurrentEvaluation);
        Assert.NotNull(against.Comparison);
    }

    [Fact]
    public void A_profile_with_no_answers_yet_is_judged_rather_than_refused()
    {
        // Every step opens on a chosen answer, but a name typed before the
        // first step is answered must not throw - the panel would go blank at
        // precisely the moment somebody is learning what it does.
        var judgement = DraftProfileJudge.Judge(
            new DraftProfileJudgementRequest(
                Name: null,
                MediaType: null,
                AllowedQualities: null,
                CutoffQuality: null,
                CustomFormatIds: null,
                FormatIntents: null,
                UpgradeUntilCutoff: true,
                UpgradeUnknownItems: false,
                AllowLowerQualityReplacements: false,
                ReleaseName: "Dune.2021.1080p.WEB-DL.x264-NTb"),
            [],
            null,
            Clock);

        Assert.NotNull(judgement.CandidateEvaluation);
        Assert.Equal("Dune.2021.1080p.WEB-DL.x264-NTb", judgement.ReleaseName);
    }

    /// <summary>
    /// The defect the product owner caught by looking at the screen.
    ///
    /// <para>A profile allowing WEB 1080p, WEB 720p and HDTV 1080p was offered
    /// a Remux 2160p and the panel said <i>"Deluno would take this and stop
    /// looking"</i>. A real search would have hard-rejected it, because the
    /// allowed list is a gate — "I do not have the storage for 4K" is exactly
    /// what it is for.</para>
    ///
    /// <para>The plan itself is innocent: its quality family deliberately
    /// includes every tier at or above the cutoff so that a held file better
    /// than the whole allowed list can be placed, rather than read as below
    /// goal and queued for downgrade. That is right for ranking and says
    /// nothing about grabbing, and this endpoint was reporting the ranking as
    /// though it were the decision.</para>
    /// </summary>
    [Fact]
    public void A_release_above_the_allowed_list_is_refused_however_good_it_is()
    {
        var judgement = Judge(
            ["WEB-1080p", "WEB-720p", "HDTV-1080p"],
            "WEB-1080p",
            "Dune.2021.2160p.UHD.BluRay.REMUX.HDR.TrueHD.Atmos-FraMeSToR");

        Assert.NotNull(judgement.Refusal);
        Assert.Contains("not one of the qualities this profile allows", judgement.Refusal!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_release_inside_the_allowed_list_is_not_refused()
    {
        var judgement = Judge(["WEB-1080p", "WEB-720p"], "WEB-1080p", "Dune.2021.1080p.WEB-DL.x264-NTb");

        Assert.Null(judgement.Refusal);
        Assert.Equal(PreferenceEvaluationStatus.MeetsPlan, judgement.CandidateEvaluation.Status);
    }

    [Fact]
    public void A_profile_that_names_no_tiers_gates_nothing()
    {
        // An empty allowed list means the profile does not constrain tiers. It
        // must never be read as "nothing is allowed", which would refuse every
        // release on a profile somebody had not finished answering.
        var judgement = DraftProfileJudge.Judge(
            Request([], "WEB-1080p", "Dune.2021.2160p.UHD.BluRay.REMUX.HDR-FraMeSToR") with { AllowedQualities = [] },
            [],
            null,
            Clock);

        Assert.Null(judgement.Refusal);
    }

    private static DraftProfileJudgement Judge(string[] allowed, string cutoff, string releaseName)
        => DraftProfileJudge.Judge(Request(allowed, cutoff, releaseName), [], null, Clock);

    private static DraftProfileJudgementRequest Request(string[] allowed, string cutoff, string releaseName)
        => new(
            Name: "Draft",
            MediaType: "movies",
            AllowedQualities: allowed,
            CutoffQuality: cutoff,
            CustomFormatIds: null,
            FormatIntents: null,
            UpgradeUntilCutoff: true,
            UpgradeUnknownItems: false,
            AllowLowerQualityReplacements: false,
            ReleaseName: releaseName);
}
