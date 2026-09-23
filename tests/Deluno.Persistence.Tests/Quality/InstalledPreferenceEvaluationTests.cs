using Deluno.Quality;
using Deluno.Quality.Contracts;
using Deluno.Quality.Guides;
using Deluno.Quality.ReleasePreferences;

namespace Deluno.Persistence.Tests.Quality;

public sealed class InstalledPreferenceEvaluationTests
{
    [Theory]
    [InlineData(ProfileFormatIntents.Prefer, -1)]
    [InlineData(ProfileFormatIntents.Avoid, 1)]
    [InlineData(ProfileFormatIntents.DoNotCare, 0)]
    public void Local_custom_rule_intent_orders_matching_and_non_matching_releases_without_scores(
        string intent,
        int expectedDirection)
    {
        var now = DateTimeOffset.Parse("2026-09-15T00:00:00Z");
        var format = LocalGroupFormat(now);
        var profile = LocalRuleProfile(now, format, intent, upgradeUntilCutoff: false);
        var plan = ReleasePreferencePlanFactory.CreateQualityPlan(profile, [format]);

        var matching = ReleasePreferenceEvaluator.Evaluate(
            plan,
            ReleasePreferenceFactFactory.FromReleaseName(
                plan,
                "Movie.2026.1080p.WEB-DL.x264-DELUNO"));
        var notMatching = ReleasePreferenceEvaluator.Evaluate(
            plan,
            ReleasePreferenceFactFactory.FromReleaseName(
                plan,
                "Movie.2026.1080p.WEB-DL.x264-OTHER"));

        Assert.Equal(
            expectedDirection,
            Math.Sign(ReleasePreferenceEvaluator.CompareForSelection(plan, matching, notMatching)));
        Assert.DoesNotContain(plan.OrderedFamilies, family =>
            family.Dimension.Contains(format.Score.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public void Local_block_rule_is_a_hard_gate_and_strong_preference_can_drive_an_upgrade()
    {
        var now = DateTimeOffset.Parse("2026-09-15T00:00:00Z");
        var format = LocalGroupFormat(now);
        var blockedPlan = ReleasePreferencePlanFactory.CreateQualityPlan(
            LocalRuleProfile(now, format, ProfileFormatIntents.MustNotHave, upgradeUntilCutoff: false),
            [format]);
        var blockedFacts = ReleasePreferenceFactFactory.FromReleaseName(
            blockedPlan,
            "Movie.2026.1080p.WEB-DL.x264-DELUNO");
        var allowedFacts = ReleasePreferenceFactFactory.FromReleaseName(
            blockedPlan,
            "Movie.2026.1080p.WEB-DL.x264-OTHER");

        Assert.False(ReleasePreferenceEvaluator.Evaluate(blockedPlan, blockedFacts).HardGatesPassed);
        Assert.True(ReleasePreferenceEvaluator.Evaluate(blockedPlan, allowedFacts).HardGatesPassed);

        var strongPlan = ReleasePreferencePlanFactory.CreateQualityPlan(
            LocalRuleProfile(now, format, ProfileFormatIntents.StronglyPrefer, upgradeUntilCutoff: false),
            [format]);
        var current = ReleasePreferenceFactFactory.FromReleaseName(
            strongPlan,
            "Movie.2026.1080p.WEB-DL.x264-OTHER");
        var candidate = ReleasePreferenceFactFactory.FromReleaseName(
            strongPlan,
            "Movie.2026.1080p.WEB-DL.x264-DELUNO");
        var comparison = ReleasePreferenceEvaluator.Compare(strongPlan, current, candidate);

        Assert.Equal(PreferenceCandidateStatus.Upgrade, comparison.Status);
        Assert.True(comparison.PersistentImprovement);
    }

    [Fact]
    public void Imported_file_snapshot_records_selected_guide_matches_and_typed_facts()
    {
        var now = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
        var guideFormat = GuidePackageCatalog.Current.CustomFormats.Single(format =>
            format.MappingStatus == GuideMappingStatus.Reviewed
            && GuideFormatConditions.AppliesTo(format, "movies")
            && format.Name.Equals("TrueHD ATMOS", StringComparison.Ordinal));
        var customFormat = new CustomFormatItem(
            "local-truehd-atmos",
            guideFormat.Name,
            "movies",
            guideFormat.OriginalScore,
            guideFormat.TrashId,
            GuideFormatConditions.For(guideFormat),
            true,
            now,
            now);
        var profile = new QualityProfileItem(
            "profile-1",
            "Home theatre",
            "movies",
            "WEB 1080p",
            "WEB 1080p,Bluray 1080p",
            customFormat.Id,
            true,
            true,
            false,
            null,
            null,
            false,
            now,
            now);

        var snapshot = InstalledPreferenceEvaluationFactory.Create(
            profile,
            "movie-1",
            "library-1",
            "Movie.2020.1080p.WEB-DL.TrueHD.Atmos.mkv",
            1_000_000,
            "WEB 1080p",
            now,
            "test",
            [customFormat],
            GuidePackageCatalog.Current);

        Assert.NotNull(snapshot);
        Assert.Equal([customFormat.Id], snapshot.MatchedRuleIds);
        Assert.Contains(snapshot.Facts, fact =>
            fact.TraitId.Equals("audio.format.truehd-atmos", StringComparison.OrdinalIgnoreCase)
            && fact.State == PreferenceFactState.Present
            && fact.Evidence?.Source == "guide-custom-format");
        Assert.Contains(snapshot.Evaluation.Families, family =>
            family.SelectedLevelId?.Equals("audio-format-truehd-atmos", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public void Probe_re_evaluation_replaces_claimed_codec_audio_and_channels_but_keeps_name_evidence()
    {
        var now = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
        var profile = new QualityProfileItem(
            "profile-1",
            "Home theatre",
            "movies",
            "WEB 1080p",
            "WEB 1080p",
            string.Empty,
            true,
            true,
            false,
            null,
            null,
            false,
            now,
            now);

        var imported = InstalledPreferenceEvaluationFactory.Create(
            profile,
            "movie-1",
            "library-1",
            "Movie.2020.1080p.WEB-DL.x264.DTS.2.0-GROUP.mkv",
            1_000,
            "WEB 1080p",
            now,
            "import");

        var reevaluated = InstalledPreferenceEvaluationFactory.Create(
            profile,
            "movie-1",
            "library-1",
            "Movie.2020.1080p.WEB-DL.x264.DTS.2.0-GROUP.mkv",
            1_000,
            "WEB 1080p",
            now.AddMinutes(1),
            "library-media-probe",
            baselineFacts: imported!.Facts,
            probedVideoCodec: "HEVC",
            probedAudioCodec: "TrueHD",
            probedAudioChannels: "5.1");

        Assert.NotNull(reevaluated);
        Assert.Contains(reevaluated.Facts, fact =>
            fact.TraitId.Equals("video.codec.hevc", StringComparison.OrdinalIgnoreCase)
            && fact.Evidence?.Source == "media-probe");
        Assert.DoesNotContain(reevaluated.Facts, fact => fact.TraitId.Equals("video.codec.h264", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(reevaluated.Facts, fact => fact.TraitId.Equals("audio.format.truehd", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(reevaluated.Facts, fact => fact.TraitId.Equals("audio.format.dts", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(reevaluated.Facts, fact => fact.TraitId.Equals("audio.channels.5-1", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(reevaluated.Facts, fact => fact.TraitId.Equals("audio.channels.2-0", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(reevaluated.Facts, fact => fact.TraitId.Equals("source.webdl", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(reevaluated.Facts, fact => fact.TraitId.Equals("release-group.unclassified", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// #584: a file a processor made keeps the release's facts. The processor
    /// stripped the TrueHD track and left AC3; judged by the probe, the same
    /// release offered again would look like an upgrade, be fetched, and be
    /// stripped the same way.
    /// </summary>
    [Fact]
    public void A_processed_file_is_described_by_its_release_not_by_what_the_processor_left()
    {
        var now = DateTimeOffset.Parse("2026-09-23T00:00:00Z");
        var profile = HomeTheatre(now);
        const string release = "Movie.2020.1080p.BluRay.x264.TrueHD.7.1-GROUP";
        var imported = InstalledPreferenceEvaluationFactory.Create(
            profile, "movie-1", "library-1", "Movie (2020) [Bluray-1080p].mkv", 1_000, "Bluray-1080p", now, "import",
            releaseNameEvidence: release);

        var probed = InstalledPreferenceEvaluationFactory.Create(
            profile, "movie-1", "library-1", "Movie (2020) [Bluray-1080p].mkv", 1_000, "Bluray-1080p", now.AddMinutes(1), "library-media-probe",
            baselineFacts: imported!.Facts,
            probedVideoCodec: "H264",
            probedAudioCodec: "AC3",
            probedAudioChannels: "2.0",
            releaseNameEvidence: release,
            describedByReleaseName: true);

        Assert.Contains(probed!.Facts, fact => fact.TraitId.Equals("audio.format.truehd", StringComparison.OrdinalIgnoreCase)
            && fact.Evidence?.Source == "release-title");
        Assert.DoesNotContain(probed.Facts, fact => fact.TraitId.Equals("audio.format.ac3", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(probed.Facts, fact => fact.TraitId.Equals("audio.channels.7-1", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>What the release name does not say, the probe still does.</summary>
    [Fact]
    public void A_processed_file_takes_from_the_probe_only_what_its_release_does_not_say()
    {
        var now = DateTimeOffset.Parse("2026-09-23T00:00:00Z");
        var profile = HomeTheatre(now);
        const string release = "Movie.2020.1080p.WEB-DL-GROUP";
        var imported = InstalledPreferenceEvaluationFactory.Create(
            profile, "movie-1", "library-1", "Movie (2020).mkv", 1_000, "WEB 1080p", now, "import", releaseNameEvidence: release);

        var probed = InstalledPreferenceEvaluationFactory.Create(
            profile, "movie-1", "library-1", "Movie (2020).mkv", 1_000, "WEB 1080p", now.AddMinutes(1), "library-media-probe",
            baselineFacts: imported!.Facts,
            probedVideoCodec: "HEVC",
            probedAudioCodec: "EAC3",
            probedAudioChannels: "5.1",
            releaseNameEvidence: release,
            describedByReleaseName: true);

        Assert.Contains(probed!.Facts, fact => fact.TraitId.Equals("video.codec.hevc", StringComparison.OrdinalIgnoreCase)
            && fact.Evidence?.Source == "media-probe");
    }

    /// <summary>
    /// A file an earlier probe already rewrote is put right on its next read,
    /// not kept as it was: the fix has to reach files probed before it.
    /// </summary>
    [Fact]
    public void A_processed_file_an_earlier_probe_rewrote_is_put_right()
    {
        var now = DateTimeOffset.Parse("2026-09-23T00:00:00Z");
        var profile = HomeTheatre(now);
        const string release = "Movie.2020.1080p.BluRay.x264.TrueHD.7.1-GROUP";
        var imported = InstalledPreferenceEvaluationFactory.Create(
            profile, "movie-1", "library-1", "Movie (2020).mkv", 1_000, "Bluray-1080p", now, "import", releaseNameEvidence: release);
        var rewrittenBefore = InstalledPreferenceEvaluationFactory.Create(
            profile, "movie-1", "library-1", "Movie (2020).mkv", 1_000, "Bluray-1080p", now.AddMinutes(1), "library-media-probe",
            baselineFacts: imported!.Facts, probedAudioCodec: "AC3", releaseNameEvidence: release);
        Assert.Contains(rewrittenBefore!.Facts, fact => fact.TraitId.Equals("audio.format.ac3", StringComparison.OrdinalIgnoreCase));

        var putRight = InstalledPreferenceEvaluationFactory.Create(
            profile, "movie-1", "library-1", "Movie (2020).mkv", 1_000, "Bluray-1080p", now.AddMinutes(2), "library-media-probe",
            baselineFacts: rewrittenBefore.Facts, probedAudioCodec: "AC3", releaseNameEvidence: release, describedByReleaseName: true);

        Assert.Contains(putRight!.Facts, fact => fact.TraitId.Equals("audio.format.truehd", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(putRight.Facts, fact => fact.TraitId.Equals("audio.format.ac3", StringComparison.OrdinalIgnoreCase));
    }

    private static QualityProfileItem HomeTheatre(DateTimeOffset now)
        => new("profile-1", "Home theatre", "movies", "WEB 1080p", "WEB 1080p", string.Empty, true, true, false, null, null, false, now, now);

    [Theory]
    [InlineData("Movie.2026.1080p.NF.WEB-DL-GROUP", "service.netflix")]
    [InlineData("Movie.2026.1080p.AMZN.WEB-DL-GROUP", "service.amazon")]
    [InlineData("Movie.2026.1080p.ATVP.WEB-DL-GROUP", "service.apple")]
    [InlineData("Movie.2026.1080p.DSNP.WEB-DL-GROUP", "service.disney")]
    [InlineData("Movie.2026.1080p.HMAX.WEB-DL-GROUP", "service.max")]
    [InlineData("Movie.2026.1080p.HULU.WEB-DL-GROUP", "service.hulu")]
    public void Typed_release_facts_recognize_the_reviewed_guide_service_tags(
        string releaseName,
        string expectedTraitId)
    {
        var now = DateTimeOffset.Parse("2026-09-15T00:00:00Z");
        var profile = new QualityProfileItem(
            "profile-1",
            "Streaming services",
            "movies",
            "WEB 1080p",
            "WEB 1080p",
            string.Empty,
            true,
            false,
            false,
            null,
            null,
            false,
            now,
            now);
        var plan = ReleasePreferencePlanFactory.CreateQualityPlan(profile);

        var facts = ReleasePreferenceFactFactory.FromReleaseName(plan, releaseName);

        Assert.Contains(facts, fact =>
            fact.TraitId.Equals(expectedTraitId, StringComparison.OrdinalIgnoreCase)
            && fact.State == PreferenceFactState.Present);
    }

    [Fact]
    public void Renaming_keeps_exact_release_and_custom_format_evidence_through_probe_re_evaluation()
    {
        var now = DateTimeOffset.Parse("2026-09-15T00:00:00Z");
        var guideFormat = GuidePackageCatalog.Current.CustomFormats.Single(format =>
            format.Name.Equals("NF", StringComparison.Ordinal)
            && GuideFormatConditions.AppliesTo(format, "movies"));
        var netflix = new CustomFormatItem(
            "netflix",
            guideFormat.Name,
            "movies",
            guideFormat.OriginalScore,
            guideFormat.TrashId,
            GuideFormatConditions.For(guideFormat),
            true,
            now,
            now);
        var preferredGroup = new CustomFormatItem(
            "preferred-deluno",
            "Preferred DELUNO group",
            "movies",
            100,
            null,
            "[{\"type\":\"releaseGroup\",\"value\":\"DELUNO\",\"negate\":false,\"required\":true}]",
            true,
            now,
            now);
        var profile = new QualityProfileItem(
            "profile-1",
            "Streaming services",
            "movies",
            "WEB 1080p",
            "WEB 1080p",
            $"{netflix.Id},{preferredGroup.Id}",
            true,
            false,
            false,
            null,
            null,
            false,
            now,
            now,
            FormatIntents: new Dictionary<string, string>
            {
                [netflix.Id] = "prefer",
                [preferredGroup.Id] = "prefer"
            });
        const string destination = @"C:\media\Big Buck Bunny (2008).mkv";
        const string releaseName = "Big.Buck.Bunny.2008.1080p.NF.WEB-DL.x264-DELUNO";

        var imported = InstalledPreferenceEvaluationFactory.Create(
            profile,
            "movie-1",
            "library-1",
            destination,
            1_000,
            "WEB 1080p",
            now,
            "filesystem.import",
            [netflix, preferredGroup],
            releaseNameEvidence: releaseName);

        Assert.NotNull(imported);
        Assert.Equal(destination, imported.FilePath);
        Assert.Equal(releaseName, imported.ReleaseNameEvidence);
        Assert.Equal([netflix.Id, preferredGroup.Id], imported.MatchedRuleIds.Order(StringComparer.Ordinal).ToArray());
        Assert.Contains(imported.Facts, fact =>
            fact.TraitId.Equals("service.netflix", StringComparison.OrdinalIgnoreCase)
            && fact.Evidence?.Source == "guide-custom-format");
        Assert.Contains(imported.Facts, fact =>
            fact.TraitId.Equals("release-group.unclassified", StringComparison.OrdinalIgnoreCase)
            && fact.Evidence?.Source == "release-title"
            && fact.Evidence?.Detail?.Contains("DELUNO", StringComparison.OrdinalIgnoreCase) == true);

        var probed = InstalledPreferenceEvaluationFactory.Create(
            profile,
            "movie-1",
            "library-1",
            destination,
            1_000,
            "WEB 1080p",
            now.AddMinutes(1),
            "library-media-probe",
            [netflix, preferredGroup],
            baselineFacts: imported.Facts,
            probedVideoCodec: "AVC",
            releaseNameEvidence: imported.ReleaseNameEvidence);

        Assert.NotNull(probed);
        Assert.Equal(imported.ReleaseNameEvidence, probed.ReleaseNameEvidence);
        Assert.Equal(imported.MatchedRuleIds.Order(StringComparer.Ordinal), probed.MatchedRuleIds.Order(StringComparer.Ordinal));
        Assert.Contains(probed.Facts, fact =>
            fact.TraitId.Equals("service.netflix", StringComparison.OrdinalIgnoreCase)
            && fact.Evidence?.Source == "guide-custom-format");

        var roundTrip = ReleasePreferenceSnapshotCodec.Deserialize(
            ReleasePreferenceSnapshotCodec.Serialize(probed));
        Assert.Equal(releaseName, roundTrip.ReleaseNameEvidence);
    }

    private static CustomFormatItem LocalGroupFormat(DateTimeOffset now)
        => new(
            "preferred-deluno",
            "Preferred DELUNO group",
            "movies",
            100,
            null,
            "[{\"type\":\"releaseGroup\",\"value\":\"DELUNO\",\"negate\":false,\"required\":true}]",
            true,
            now,
            now);

    private static QualityProfileItem LocalRuleProfile(
        DateTimeOffset now,
        CustomFormatItem format,
        string intent,
        bool upgradeUntilCutoff)
        => new(
            "profile-local-rule",
            "Local rule",
            "movies",
            "WEB 1080p",
            "WEB 1080p",
            format.Id,
            upgradeUntilCutoff,
            false,
            false,
            null,
            null,
            false,
            now,
            now,
            FormatIntents: new Dictionary<string, string> { [format.Id] = intent });
}
