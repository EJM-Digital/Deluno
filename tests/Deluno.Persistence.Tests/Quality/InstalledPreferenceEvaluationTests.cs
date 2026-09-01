using Deluno.Quality.Contracts;
using Deluno.Quality.Guides;
using Deluno.Quality.ReleasePreferences;

namespace Deluno.Persistence.Tests.Quality;

public sealed class InstalledPreferenceEvaluationTests
{
    [Fact]
    public void Imported_file_snapshot_records_selected_guide_matches_and_typed_facts()
    {
        var now = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
        var guideFormat = GuidePackageCatalog.Current.CustomFormats.First(format =>
            format.MappingStatus == GuideMappingStatus.Reviewed
            && format.Name.Equals("TrueHD Atmos", StringComparison.OrdinalIgnoreCase));
        var customFormat = new CustomFormatItem(
            "local-truehd-atmos",
            guideFormat.Name,
            "movies",
            guideFormat.OriginalScore,
            guideFormat.TrashId,
            string.Join(Environment.NewLine, guideFormat.Patterns.Select(pattern => $"regex: {pattern}")),
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
}
