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
    public void Repairs_ocr_mistakes_only_inside_words()
    {
        // Only the confusions that can be repaired without a dictionary: a
        // standalone "l" is a mis-read "I", and a digit wedged between letters
        // is a letter. Everything else stays as it was found.
        var source =
            "1\r\n00:00:01,000 --> 00:00:03,000\r\nl think N0body knew.\r\n\r\n"
            + "2\r\n00:00:04,000 --> 00:00:06,000\r\nlet me turn the corner.\r\n\r\n";

        var result = SubtitleContentModifier.Apply(
            Encoding.UTF8.GetBytes(source),
            new SubtitleContentModificationPolicy(FixOcrErrors: true));

        var rewritten = Encoding.UTF8.GetString(result.Content);
        Assert.True(result.Modified);
        Assert.Contains("OCR errors", result.AppliedRules);
        Assert.Contains("I think Nobody knew.", rewritten, StringComparison.Ordinal);

        // The rules that would have corrupted these are deliberately absent:
        // "l" before a lowercase letter would make "let" into "Iet", and
        // "rn" to "m" would make "corner" into "comer".
        Assert.Contains("let me turn the corner.", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void Moves_trailing_punctuation_on_a_right_to_left_line_only()
    {
        var source =
            "1\r\n00:00:01,000 --> 00:00:03,000\r\nمرحبا بالعالم.\r\n\r\n"
            + "2\r\n00:00:04,000 --> 00:00:06,000\r\nHello world.\r\n\r\n";

        var result = SubtitleContentModifier.Apply(
            Encoding.UTF8.GetBytes(source),
            new SubtitleContentModificationPolicy(ReverseRightToLeftPunctuation: true));

        var rewritten = Encoding.UTF8.GetString(result.Content);
        Assert.True(result.Modified);
        Assert.Contains("right-to-left punctuation", result.AppliedRules);
        Assert.Contains(".مرحبا بالعالم", rewritten, StringComparison.Ordinal);
        // The English line carries no right-to-left character, so the policy
        // does not touch it however it is configured.
        Assert.Contains("Hello world.", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void Colours_a_cue_around_the_finished_line()
    {
        // Colour is applied last, so the tag wraps the cleaned text rather
        // than being stripped by a style-tag pass running after it.
        var source = "1\r\n00:00:01,000 --> 00:00:03,000\r\n<i>Hello</i>\r\n\r\n";

        var result = SubtitleContentModifier.Apply(
            Encoding.UTF8.GetBytes(source),
            new SubtitleContentModificationPolicy(RemoveStyleTags: true, CueColour: "Yellow"));

        var rewritten = Encoding.UTF8.GetString(result.Content);
        Assert.True(result.Modified);
        Assert.Contains("cue colour", result.AppliedRules);
        Assert.Contains("<font color=\"yellow\">Hello</font>", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("<i>", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void A_blank_colour_is_not_a_policy()
    {
        // Whitespace in a text box must not count as "enabled", or an empty
        // field would start rewriting every cue in the library.
        Assert.Null(SubtitleContentModificationPolicyCodec.Normalize(
            new SubtitleContentModificationPolicy(CueColour: "   ")));
        Assert.Equal(
            "yellow",
            SubtitleContentModificationPolicyCodec.Normalize(
                new SubtitleContentModificationPolicy(CueColour: " Yellow "))!.CueColour);
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
