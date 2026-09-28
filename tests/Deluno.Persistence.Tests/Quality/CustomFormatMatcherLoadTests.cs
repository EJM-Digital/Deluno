using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Deluno.Quality.Guides;
using Deluno.Quality.ReleasePreferences;

namespace Deluno.Persistence.Tests.Quality;

/// <summary>
/// #940: a custom format's answer must not depend on how busy the machine is.
/// A 100 ms wall-clock regex timeout turned paused threads into "no match",
/// which dropped [NF] from an import's file name on a loaded CI runner.
/// </summary>
public sealed class CustomFormatMatcherLoadTests
{
    private const string Release = "Sintel.2010.1080p.NF.WEB-DL.DDP5.1.H.264-DELUNO";

    [Fact]
    public void A_matching_release_stays_matched_when_many_threads_share_the_machine()
    {
        var netflix = GuidePackageCatalog.Current.CustomFormats.Single(format =>
            format.Name.Equals("NF", StringComparison.Ordinal)
            && GuideFormatConditions.AppliesTo(format, "movies"));
        var conditions = GuideFormatConditions.For(netflix);
        Assert.True(CustomFormatDefinitionMatcher.Evaluate(Release, conditions, anyCondition: true, "movies").IsMatch);

        // Far more threads than cores, each pausing the others mid-match. The
        // old matcher returned "no match" 18 times in 4.5 million under this.
        var threads = Environment.ProcessorCount * 16;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        long evaluations = 0;
        long misses = 0;
        var workers = Enumerable.Range(0, threads).Select(_ => new Thread(() =>
        {
            long localEvaluations = 0;
            long localMisses = 0;
            while (DateTime.UtcNow < deadline)
            {
                localEvaluations++;
                if (!CustomFormatDefinitionMatcher.Evaluate(Release, conditions, anyCondition: true, "movies").IsMatch)
                {
                    localMisses++;
                }
            }

            Interlocked.Add(ref evaluations, localEvaluations);
            Interlocked.Add(ref misses, localMisses);
        })).ToList();
        workers.ForEach(worker => worker.Start());
        workers.ForEach(worker => worker.Join());

        Assert.True(evaluations > 0);
        Assert.Equal(0, misses);
    }

    [Fact]
    public void An_invalid_imported_regex_still_matches_as_literal_text()
    {
        const string conditions = "x264-[GRP";

        Assert.True(CustomFormatDefinitionMatcher.Evaluate("Movie.2026.1080p.BluRay.x264-[GRP", conditions, anyCondition: true, "movies").IsMatch);
        Assert.False(CustomFormatDefinitionMatcher.Evaluate("Movie.2026.1080p.BluRay.x264-OTHER", conditions, anyCondition: true, "movies").IsMatch);
    }

    [Fact]
    public void Every_guide_pattern_answers_the_same_on_the_linear_engine_as_on_the_backtracking_one()
    {
        string[] releases =
        [
            Release,
            "Arrival.2016.1080p.BluRay.DTS.x264-GRP",
            "Dune.Part.Two.2024.2160p.AMZN.WEB-DL.DDP5.1.Atmos.DV.HDR10.H.265-FLUX",
            "Oppenheimer.2023.IMAX.2160p.UHD.BluRay.REMUX.HDR10.TrueHD.7.1.Atmos-FraMeSToR",
            "The.Office.US.S03E05.720p.NF.WEBRip.DDP5.1.x264-NTb",
            "Blade.Runner.1982.Final.Cut.1080p.BluRay.x264-SPARKS",
            "Movie.2026.MULTI.1080p.WEB-DL.AV1.Opus-GRP",
            "Movie.2026.1080p.HDTV.x264-LOL",
            "Movie.2026.CAM.x264-YIFY",
            "Movie.2026.2160p.ATVP.WEB-DL.DDP5.1.HDR.HEVC-CMRG",
            "Movie.2026.1080p.DSNP.WEB-DL.DDP5.1.H.264-EVO",
            "Movie.2026.REPACK.PROPER.1080p.BluRay.x265.10bit-Tigole",
            "Movie.2026.Extended.Directors.Cut.Remastered.1080p.BluRay-GRP",
            "Movie.2026.1080p.BluRay.x264-[YTS.MX]",
            "Movie 2026 1080p HMAX WEB-DL DD5.1 H 264-playWEB",
            "Movie.2026.480p.DVDRip.XviD-AMIABLE",
            "Movie.2026.1080p.WEB.h264-SiGMA",
            "Movie.2026.3D.HSBS.1080p.BluRay.x264-GRP",
        ];

        var patterns = GuidePackageCatalog.Current.CustomFormats
            .SelectMany(format => (format.Patterns ?? [])
                .Concat((format.SourceMatcherClauses ?? []).SelectMany(clause => StringValues(clause.FieldsJson))))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(patterns);

        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        var compared = 0;
        foreach (var pattern in patterns)
        {
            Regex linear;
            Regex backtracking;
            try
            {
                linear = new Regex(pattern, options | RegexOptions.NonBacktracking);
                backtracking = new Regex(pattern, options);
            }
            catch (Exception exception) when (exception is NotSupportedException or ArgumentException)
            {
                continue;
            }

            foreach (var release in releases)
            {
                Assert.True(
                    linear.IsMatch(release) == backtracking.IsMatch(release),
                    $"'{pattern}' disagrees between engines on '{release}'.");
            }

            compared++;
        }

        Assert.True(compared > 0);
    }

    private static IEnumerable<string> StringValues(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || JsonNode.Parse(json) is not { } root)
        {
            yield break;
        }

        var pending = new Stack<JsonNode?>([root]);
        while (pending.Count > 0)
        {
            switch (pending.Pop())
            {
                case JsonObject obj:
                    foreach (var property in obj)
                    {
                        pending.Push(property.Value);
                    }

                    break;
                case JsonArray array:
                    foreach (var item in array)
                    {
                        pending.Push(item);
                    }

                    break;
                case JsonValue value when value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text):
                    yield return text;
                    break;
            }
        }
    }
}
