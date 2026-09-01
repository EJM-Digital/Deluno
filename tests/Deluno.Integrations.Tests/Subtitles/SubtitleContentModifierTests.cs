using System.Text;
using Deluno.Contracts;
using Deluno.Integrations.Subtitles;

namespace Deluno.Integrations.Tests.Subtitles;

public sealed class SubtitleContentModifierTests
{
    [Fact]
    public void Applies_named_text_cleanups_without_changing_timestamps_or_indexes()
    {
        var source = "1\r\n00:00:01,000 --> 00:00:03,000\r\n<i>HELLO   WORLD</i> 😀\r\n[MUSIC]\r\n\r\n";

        var result = SubtitleContentModifier.Apply(
            Encoding.UTF8.GetBytes(source),
            new SubtitleContentModificationPolicy(
                StripHearingImpairedAnnotations: true,
                RemoveStyleTags: true,
                RemoveEmoji: true,
                NormalizeWhitespace: true,
                FixAllUppercase: true));

        var rewritten = Encoding.UTF8.GetString(result.Content);
        Assert.True(result.Modified);
        Assert.Equal(
            ["all-uppercase text", "emoji", "hearing-impaired annotations", "style tags", "whitespace"],
            result.AppliedRules);
        Assert.Contains("1\r\n00:00:01,000 --> 00:00:03,000\r\nHello world\r\n\r\n", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("😀", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("[MUSIC]", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void Leaves_short_acronyms_and_non_hearing_brackets_alone()
    {
        var source = "1\n00:00:01,000 --> 00:00:03,000\nNASA [Project Dawn]\n\n";

        var result = SubtitleContentModifier.Apply(
            Encoding.UTF8.GetBytes(source),
            new SubtitleContentModificationPolicy(
                StripHearingImpairedAnnotations: true,
                FixAllUppercase: true));

        Assert.False(result.Modified);
        Assert.Empty(result.AppliedRules);
        Assert.Equal(Encoding.UTF8.GetBytes(source), result.Content);
    }

    [Fact]
    public void Does_not_rewrite_unknown_subtitle_formats()
    {
        var source = Encoding.UTF8.GetBytes("[Script Info]\nTitle: Example\n");

        var result = SubtitleContentModifier.Apply(
            source,
            new SubtitleContentModificationPolicy(RemoveStyleTags: true));

        Assert.False(result.Modified);
        Assert.Empty(result.AppliedRules);
        Assert.Same(source, result.Content);
    }

    [Fact]
    public void A_disabled_or_empty_policy_returns_the_original_bytes()
    {
        var source = Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:03,000\nHELLO\n");

        var disabled = SubtitleContentModifier.Apply(source, new SubtitleContentModificationPolicy());
        var absent = SubtitleContentModifier.Apply(source, null);

        Assert.Same(source, disabled.Content);
        Assert.Same(source, absent.Content);
        Assert.False(disabled.Modified);
        Assert.False(absent.Modified);
    }
}
