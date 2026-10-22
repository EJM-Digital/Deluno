using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Quality;
using Deluno.Quality.ReleasePreferences;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Quality;

/// <summary>
/// What Deluno reads out of a release name before it decides anything: the quality tier, the language, the dynamic range,
/// the resolution, the bit depth, the edition and the unwanted markers (upscaled, CAM, telesync, screener, sample). Real
/// parsing code, no stand-ins. Refs #1296.
/// </summary>
public sealed class WhatAReleaseNameSaysTests
{
    private static ReleasePreferencePlan Plan()
        => ReleasePreferencePlanFactory.CreateQualityPlan("movies", "WEB 1080p", ["WEB 1080p", "Bluray 1080p"]);

    /// <summary>The trait ids the name was read as, among the facts Deluno recorded as present.</summary>
    private static HashSet<string> Present(string releaseName)
        => ReleasePreferenceFactFactory.FromReleaseName(Plan(), releaseName)
            .Where(fact => fact.State == PreferenceFactState.Present)
            .Select(fact => fact.NormalizedTraitId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string Trait(string dimension, string value)
    {
        Assert.True(PreferenceTraitRegistry.Current.TryResolveObserved(dimension, value, out var definition), $"{dimension} '{value}' is a known trait");
        return definition.Id;
    }

    [Theory]
    [InlineData("Dune.2021.2160p.UHD.BluRay.DV.HDR10.x265-GRP", "Dolby Vision with HDR10 fallback")]
    [InlineData("Dune.2021.2160p.WEB-DL.DoVi.x265-GRP", "Dolby Vision")]
    [InlineData("Dune.2021.2160p.WEB-DL.Dolby.Vision.x265-GRP", "Dolby Vision")]
    [InlineData("Dune.2021.2160p.WEB-DL.HDR10+.x265-GRP", "HDR10+")]
    [InlineData("Dune.2021.2160p.WEB-DL.HDR10Plus.x265-GRP", "HDR10+")]
    [InlineData("Dune.2021.2160p.WEB-DL.HDR10.x265-GRP", "HDR10")]
    [InlineData("Dune.2021.2160p.WEB-DL.HLG.x265-GRP", "HLG")]
    [InlineData("Dune.2021.1080p.BluRay.SDR.x264-GRP", "SDR")]
    public void The_dynamic_range_in_a_name_is_read(string name, string range)
        => Assert.Contains(Trait("video.dynamic-range", range), Present(name));

    [Theory]
    [InlineData("Dune.2021.2160p.WEB-DL.x265-GRP", "2160p")]
    [InlineData("Dune.2021.4K.WEB-DL.x265-GRP", "2160p")]
    [InlineData("Dune.2021.1080p.WEB-DL.x264-GRP", "1080p")]
    [InlineData("Dune.2021.720p.WEB-DL.x264-GRP", "720p")]
    [InlineData("Dune.1984.576p.DVDRip.x264-GRP", "576p")]
    [InlineData("Dune.1984.480p.DVDRip.x264-GRP", "480p")]
    public void The_resolution_in_a_name_is_read(string name, string resolution)
        => Assert.Contains(Trait("video.resolution", resolution), Present(name));

    [Theory]
    [InlineData("Dune.2021.2160p.WEB-DL.10bit.x265-GRP", "10bit")]
    [InlineData("Dune.2021.2160p.WEB-DL.10-bit.x265-GRP", "10bit")]
    [InlineData("Dune.2021.1080p.WEB-DL.8bit.x264-GRP", "8bit")]
    [InlineData("Dune.2021.2160p.WEB-DL.12bit.x265-GRP", "12bit")]
    public void The_bit_depth_in_a_name_is_read(string name, string depth)
        => Assert.Contains(Trait("video.bit-depth", depth), Present(name));

    [Theory]
    [InlineData("Blade.Runner.1982.Directors.Cut.1080p.BluRay.x264-GRP", "Director's cut")]
    [InlineData("Blade.Runner.1982.Extended.1080p.BluRay.x264-GRP", "Extended cut")]
    [InlineData("Dune.2021.IMAX.1080p.WEB-DL.x264-GRP", "IMAX")]
    [InlineData("Dune.2021.UNCUT.1080p.WEB-DL.x264-GRP", "Uncut")]
    public void The_edition_in_a_name_is_read(string name, string edition)
        => Assert.Contains(Trait("edition", edition), Present(name));

    [Theory]
    [InlineData("Dune.2021.1080p.Upscaled.WEB-DL.x264-GRP", "Upscaled")]
    [InlineData("Dune.2021.CAMRip.x264-GRP", "CAM")]
    [InlineData("Dune.2021.TELESYNC.x264-GRP", "Telesync")]
    [InlineData("Dune.2021.SCREENER.x264-GRP", "Screener")]
    [InlineData("Dune.2021.1080p.Sample.mkv", "Sample")]
    public void An_unwanted_marker_in_a_name_is_read(string name, string marker)
        => Assert.Contains(Trait("unwanted", marker), Present(name));

    [Fact]
    public void A_clean_release_carries_none_of_the_unwanted_markers()
    {
        var present = Present("Dune.2021.1080p.BluRay.x264-GRP");
        foreach (var marker in new[] { "Upscaled", "CAM", "Telesync", "Screener", "Sample" })
        {
            Assert.DoesNotContain(Trait("unwanted", marker), present);
        }
    }

    [Theory]
    [InlineData("Movie.2026.1080p.WEB-DL.MULTI-GRP", "Multilingual")]
    [InlineData("Movie.2026.1080p.WEB-DL.DUAL-GRP", "Dubbed")]
    [InlineData("Movie.2026.1080p.WEB-DL.FRENCH-GRP", "French")]
    [InlineData("Movie.2026.1080p.WEB-DL.VOSTFR-GRP", "French")]
    [InlineData("Movie.2026.1080p.WEB-DL.GERMAN-GRP", "German")]
    [InlineData("Movie.2026.1080p.WEB-DL.JPN-GRP", "Japanese")]
    [InlineData("Movie.2026.1080p.WEB-DL.SPANISH-GRP", "Spanish")]
    [InlineData("Movie.2026.1080p.WEB-DL.LATINO-GRP", "Spanish")]
    [InlineData("Movie.2026.1080p.WEB-DL.ENGLISH-GRP", "English")]
    public void A_language_tag_in_a_name_is_read_as_its_own_word(string name, string language)
        => Assert.Equal(language, ReleasePreferenceFactFactory.DetectLanguageFromReleaseName(name));

    [Theory]
    [InlineData("Movie.2026.1080p.WEB-DL.x264-GRP")]
    [InlineData("Multiverse.of.Madness.2022.1080p.WEB-DL.x264-GRP")]
    [InlineData("The.Dualist.2020.1080p.WEB-DL.x264-GRP")]
    public void A_name_with_no_language_tag_says_no_language(string name)
        => Assert.Null(ReleasePreferenceFactFactory.DetectLanguageFromReleaseName(name));

    /// <summary>
    /// The row says a language word fused inside a title does not count. These words are inside the title: the film
    /// "Frenchman", the "Spanish Prisoner" is its own word and so counts, but "Englishman" is not "English". The words
    /// "french", "german", "spanish", "english", "japanese" and "original" are matched anywhere in the name.
    /// </summary>
    [Theory]
    [InlineData("The.Englishman.Who.Went.Up.A.Hill.1995.1080p.BluRay.x264-GRP")]
    [InlineData("Frenchman.s.Creek.1944.1080p.BluRay.x264-GRP")]
    [InlineData("Original.Sin.2001.1080p.BluRay.x264-GRP")]
    public void A_language_word_fused_inside_a_title_does_not_count(string name)
        => Assert.Null(ReleasePreferenceFactFactory.DetectLanguageFromReleaseName(name));

    [Theory]
    [InlineData("Movie.2020.1080p.BluRay.x264-GRP-sample.mkv")]
    [InlineData("Movie.2020.1080p.BluRay.x264-GRP/sample/movie-sample.mkv")]
    [InlineData("Movie.2020.1080p.BluRay.x264-GRP.Sample.mkv")]
    public void A_sample_marker_after_the_title_is_still_a_sample_clip(string name)
        => Assert.Contains(Trait("unwanted", "Sample"), Present(name));

    [Theory]
    [InlineData("Movie.2026.1080p.WEB-DL.GERMAN-GRP", "German")]
    [InlineData("Movie.2026.1080p.WEB-DL.SPANISH-GRP", "Spanish")]
    [InlineData("Movie.2026.1080p.WEB-DL.ENGLISH-GRP", "English")]
    [InlineData("Movie.2026.1080p.WEB-DL.JAPANESE-GRP", "Japanese")]
    [InlineData("Movie.2026.1080p.BluRay.FRENCH.x264-GRP", "French")]
    [InlineData("Movie.2026.1080p.BluRay.MULTi.x264-GRP", "Multilingual")]
    [InlineData("Show.S01E01.1080p.WEB-DL.TRUEFRENCH-GRP", "French")]
    public void A_language_tag_after_the_title_is_still_read(string name, string language)
        => Assert.Equal(language, ReleasePreferenceFactFactory.DetectLanguageFromReleaseName(name));

    [Theory]
    [InlineData("Sample.mkv")]
    public void The_sample_marker_belongs_to_a_sample_clip_only(string name)
        => Assert.Contains(Trait("unwanted", "Sample"), Present(name));

    [Fact]
    public void A_film_whose_title_is_the_word_sample_is_not_marked_as_a_sample_clip()
        => Assert.DoesNotContain(Trait("unwanted", "Sample"), Present("The.Sample.2020.1080p.BluRay.x264-GRP"));

    [Theory]
    [InlineData("Movie.2026.1080p.WEB-DL.FRENCH-GRP", "French")]
    [InlineData("Movie.2026.1080p.WEB-DL.MULTI-GRP", "Multilingual")]
    [InlineData("Movie.2026.1080p.WEB-DL.x264-GRP", null)]
    public async Task The_language_a_grab_is_written_with_is_the_one_the_name_gives(string releaseName, string? language)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var film = await ApiScenes.FilmOnDiskAsync(app, "Movie", 2026);

        var id = await app.Services.GetRequiredService<IJobQueueRepository>().RecordDownloadDispatchAsync(
            film.LibraryId, "movies", "movie", film.MovieId, releaseName, "Fixture indexer",
            "client-1", "Fixture client", "sent", null, cancellationToken: CancellationToken.None);

        Assert.Equal(language ?? string.Empty, await ApiScenes.TextAsync(app, "jobs", $"SELECT decision_language FROM download_dispatches WHERE id = '{id}'") ?? string.Empty);
        Assert.True(File.Exists(film.FilePath));
    }
}
