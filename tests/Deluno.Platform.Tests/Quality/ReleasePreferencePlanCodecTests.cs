using System.Text.Json;
using Deluno.Quality.ReleasePreferences;

namespace Deluno.Platform.Tests.Quality;

public sealed class ReleasePreferencePlanCodecTests
{
    private static PreferenceFamily Audio(params string[] traits)
        => new(
            "audio",
            "Audio format",
            1,
            PreferenceIntent.Ranked,
            traits.Select((trait, index) => new PreferenceFamilyLevel($"level-{index}", index, [trait])).ToArray(),
            TargetLevelId: "level-0");

    private static ReleasePreferencePlan Plan(PreferenceFamily? family = null)
        => new("plan/codec", "1", "movies", [family ?? Audio("audio.format.truehd", "audio.format.ac3")]);

    [Fact]
    public void A_missing_plan_is_refused()
        => Assert.Throws<ArgumentNullException>(() => ReleasePreferencePlanCodec.Serialize(null!));

    [Fact]
    public void A_plan_that_breaks_the_rules_is_never_written()
        => Assert.Throws<ArgumentException>(() => ReleasePreferencePlanCodec.Serialize(Plan() with { Id = " " }));

    [Fact]
    public void A_plan_with_no_optional_parts_comes_back_with_them_empty()
    {
        var json = ReleasePreferencePlanCodec.Serialize(Plan());

        var restored = ReleasePreferencePlanCodec.Deserialize(json);

        Assert.Equal("plan/codec", restored.Id);
        Assert.Empty(restored.RequiredTraitIds!);
        Assert.Empty(restored.ForbiddenTraitIds!);
        Assert.Empty(restored.RequiredAnyTraitGroups!);
        Assert.Empty(restored.Relationships!);
        Assert.Empty(restored.Overrides!);
        Assert.Empty(restored.Sources!);
        Assert.Null(restored.DimensionOrder);
    }

    [Fact]
    public void The_same_plan_in_another_order_is_written_the_same_way()
    {
        var first = Plan(Audio("audio.format.truehd", "audio.format.ac3")) with
        {
            RequiredTraitIds = ["video.dynamic-range.hdr10", " audio.format.opus "],
            ForbiddenTraitIds = ["audio.format.eac3", "audio.format.eac3", "audio.format.dtsx"],
            RequiredAnyTraitGroups =
            [
                ["video.dynamic-range.dolby-vision", "video.dynamic-range.hdr10"],
                ["audio.format.dts-hd-ma", " audio.format.dts-hd-ma "]
            ],
            Relationships =
            [
                new PreferenceRelationship("audio.format.truehd", "audio.format.opus", PreferenceRelationshipKind.Implies),
                new PreferenceRelationship("audio.format.truehd", "audio.format.ac3", PreferenceRelationshipKind.Implies),
                new PreferenceRelationship("audio.format.truehd", "audio.format.dtsx", PreferenceRelationshipKind.Incompatible)
            ],
            DimensionOrder = [" audio "],
            Overrides = new Dictionary<string, string> { ["Zed"] = " b ", ["alpha"] = " a" },
            Sources =
            [
                new PreferencePlanProvenance("Guide", "b", "v1"),
                new PreferencePlanProvenance("guide", "a", "v1", MappedTraitIds: ["audio.format.truehd", "audio.format.ac3"])
            ]
        };
        var second = first with
        {
            Families = [Audio("audio.format.truehd", "audio.format.ac3") with { Levels = [.. first.Families[0].Levels.Reverse()] }],
            RequiredTraitIds = [.. first.RequiredTraitIds!.Reverse()],
            ForbiddenTraitIds = [.. first.ForbiddenTraitIds!.Reverse()],
            RequiredAnyTraitGroups = [.. first.RequiredAnyTraitGroups!.Reverse()],
            Relationships = [.. first.Relationships!.Reverse()],
            Overrides = new Dictionary<string, string> { ["alpha"] = "a", ["zed"] = "b" },
            Sources = [.. first.Sources!.Reverse()]
        };

        var written = ReleasePreferencePlanCodec.Serialize(first);

        Assert.Equal(written, ReleasePreferencePlanCodec.Serialize(second));
    }

    [Fact]
    public void Each_part_of_a_plan_is_trimmed_lowercased_deduplicated_and_sorted_when_written()
    {
        var plan = Plan() with
        {
            RequiredTraitIds = ["video.dynamic-range.hdr10", " audio.format.opus ", "audio.format.opus"],
            ForbiddenTraitIds = ["audio.format.eac3"],
            RequiredAnyTraitGroups =
            [
                ["video.dynamic-range.hdr10", "video.dynamic-range.dolby-vision", "video.dynamic-range.hdr10"],
                ["audio.format.dts-hd-ma"]
            ],
            Relationships =
            [
                new PreferenceRelationship(" audio.format.truehd ", "audio.format.ac3", PreferenceRelationshipKind.Implies)
            ],
            DimensionOrder = [" audio "],
            Overrides = new Dictionary<string, string> { ["Zed"] = " b ", ["alpha"] = " a" },
            Sources =
            [
                new PreferencePlanProvenance(
                    " Guide ", " Rule-B ", " V2 ",
                    OriginalScore: " 1 ", AssignedScore: " ", MappingId: " m ", MappingVersion: null, Layer: " ",
                    MatcherDefinition: " x ", MappedTraitIds: ["audio.format.truehd", "audio.format.ac3"],
                    RuleId: " 7 ", RuleName: " Name ", ProfileIntent: " PREFER ")
            ]
        };

        var restored = ReleasePreferencePlanCodec.Deserialize(ReleasePreferencePlanCodec.Serialize(plan));

        Assert.Equal(["audio.format.opus", "video.dynamic-range.hdr10"], restored.RequiredTraitIds);
        Assert.Equal(["audio.format.dts-hd-ma"], restored.RequiredAnyTraitGroups![0]);
        Assert.Equal(["video.dynamic-range.dolby-vision", "video.dynamic-range.hdr10"], restored.RequiredAnyTraitGroups[1]);
        Assert.Equal("audio.format.truehd", restored.Relationships!.Single().FromTraitId);
        Assert.Equal(["audio"], restored.DimensionOrder);
        Assert.Equal(["alpha", "zed"], restored.Overrides!.Keys);
        Assert.Equal(["a", "b"], restored.Overrides.Values);
        var source = Assert.Single(restored.Sources!);
        Assert.Equal(("guide", "rule-b", "v2"), (source.SourceKind, source.SourceId, source.SourceVersion));
        Assert.Equal("1", source.OriginalScore);
        Assert.Null(source.AssignedScore);
        Assert.Equal("m", source.MappingId);
        Assert.Null(source.MappingVersion);
        Assert.Null(source.Layer);
        Assert.Equal("x", source.MatcherDefinition);
        Assert.Equal("7", source.RuleId);
        Assert.Equal("Name", source.RuleName);
        Assert.Equal("prefer", source.ProfileIntent);
        Assert.Equal(["audio.format.ac3", "audio.format.truehd"], source.MappedTraitIds);
    }

    [Fact]
    public void An_override_with_no_text_is_written_as_empty()
    {
        var plan = Plan() with { Overrides = new Dictionary<string, string> { ["k"] = null! } };

        var restored = ReleasePreferencePlanCodec.Deserialize(ReleasePreferencePlanCodec.Serialize(plan));

        Assert.Equal(string.Empty, restored.Overrides!["k"]);
    }

    [Fact]
    public void Plans_whose_sources_differ_only_in_one_detail_keep_a_fixed_order()
    {
        PreferencePlanProvenance Source(Func<PreferencePlanProvenance, PreferencePlanProvenance> change)
            => change(new PreferencePlanProvenance("guide", "rule", "v1"));

        PreferencePlanProvenance[] variants =
        [
            Source(s => s with { SourceVersion = "v2" }),
            Source(s => s with { MappingId = "m" }),
            Source(s => s with { MappingVersion = "m1" }),
            Source(s => s with { Layer = "l" }),
            Source(s => s with { MatcherDefinition = "d" }),
            Source(s => s with { MatcherAny = true }),
            Source(s => s with { OriginalScore = "1" }),
            Source(s => s with { AssignedScore = "2" }),
            Source(s => s with { RuleId = "3" }),
            Source(s => s with { RuleName = "n" }),
            Source(s => s with { ProfileIntent = "prefer" }),
            Source(s => s with { MappedTraitIds = ["audio.format.ac3"] }),
            Source(s => s)
        ];

        var forward = ReleasePreferencePlanCodec.Serialize(Plan() with { Sources = variants });
        var backward = ReleasePreferencePlanCodec.Serialize(Plan() with { Sources = [.. variants.Reverse()] });

        Assert.Equal(forward, backward);
    }

    [Fact]
    public void Text_that_is_not_a_plan_is_refused()
    {
        Assert.Throws<JsonException>(() => ReleasePreferencePlanCodec.Deserialize("null"));
        Assert.Throws<JsonException>(() => ReleasePreferencePlanCodec.Deserialize("not json"));
    }

    [Fact]
    public void A_stored_plan_that_no_longer_follows_the_rules_is_refused()
    {
        var json = ReleasePreferencePlanCodec.Serialize(Plan()).Replace("\"plan/codec\"", "\"\"");

        Assert.Throws<ArgumentException>(() => ReleasePreferencePlanCodec.Deserialize(json));
    }
}
