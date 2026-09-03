using System.Text;
using System.Text.RegularExpressions;
using Deluno.Contracts;

namespace Deluno.Integrations.Subtitles;

/// <summary>
/// Applies the configured, non-timing subtitle cleanups to SRT-shaped content.
///
/// <para>The modifier is intentionally conservative. It only changes cue body
/// lines after a recognised timing line; indexes, timestamps, WEBVTT headers and
/// unknown subtitle formats are left alone. A provider response that cannot be
/// safely understood is therefore still handled by the existing archive and
/// validity checks rather than being damaged by a best-effort rewrite.</para>
/// </summary>
public static partial class SubtitleContentModifier
{
    private static readonly Regex TimingLine = new(
        @"^\s*\d{1,3}:\d{2}:\d{2}[,.]\d{1,3}\s*-->\s*\d{1,3}:\d{2}:\d{2}[,.]\d{1,3}(?:\s+.*)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex StyleTag = new(
        @"</?(?:i|b|u|s|font|ruby|rt|c)(?:\s+[^>]*)?>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex HearingImpairedAnnotation = new(
        @"(?<open>[\[(])\s*(?<body>[^\]\)]{1,100}?)\s*(?<close>[\]\)])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex Whitespace = new(@"[ \t]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // The two OCR confusions that can be repaired without a dictionary.
    //
    // A lowercase "l" standing alone, or before an apostrophe, is a mis-read
    // "I": lowercase l is not an English word. The tempting third rule —
    // "l" before any lowercase letter — turns "let" into "Iet", and the
    // fourth, "rn" to "m", turns "corner" into "comer". Both were written,
    // both were caught by their own test, and both are gone: an OCR fix that
    // corrupts correct subtitles is worse than no OCR fix.
    private static readonly Regex OcrStandaloneLForI = new(
        @"\bl\b(?!['’])|\bl(?=['’])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // A digit wedged between letters is not a digit. The replacement takes
    // its case from the letter in front of it, because "N0body" is "Nobody"
    // and not "NObody".
    private static readonly Regex OcrDigitInsideWord = new(
        @"(?<=\p{L})(?<digit>[01])(?=\p{L})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Hebrew, Arabic, Syriac, Thaana and the Arabic presentation forms. A line
    // containing one of these is right-to-left; a line without is not,
    // whatever the policy says.
    private static readonly Regex RightToLeftCharacter = new(
        @"[֐-׿؀-ۿ܀-ݏހ-޿ࢠ-ࣿיִ-﷿ﹰ-﻿]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TrailingRightToLeftPunctuation = new(
        @"^(?<body>.*?)(?<punct>[.!?،؛؟]+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SoundAnnotation = new(
        @"(?:\b(?:music|singing|sings|song|laugh(?:s|ing)?|cry(?:ing|ing)?|sigh(?:s|ing)?|groan(?:s|ing)?|gasp(?:s|ing)?|breath(?:es|ing)?|pant(?:s|ing)?|cough(?:s|ing)?|sneeze(?:s|ing)?|sniff(?:s|ing)?|door|bell|phone|ring(?:s|ing)?|applause|clap(?:s|ping)?|cheer(?:s|ing)?|whisper(?:s|ing)?|shout(?:s|ing)?|yell(?:s|ing)?|inaudible|indistinct|speaking foreign language)\b|♪)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static SubtitleContentModificationResult Apply(
        byte[] content,
        SubtitleContentModificationPolicy? policy)
    {
        ArgumentNullException.ThrowIfNull(content);

        var normalizedPolicy = SubtitleContentModificationPolicyCodec.Normalize(policy);
        if (normalizedPolicy is null || content.Length == 0)
        {
            return new SubtitleContentModificationResult(content, [], false);
        }

        var text = Decode(content);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var hasCue = lines.Any(line => TimingLine.IsMatch(line));
        if (!hasCue)
        {
            return new SubtitleContentModificationResult(content, [], false);
        }

        var applied = new HashSet<string>(StringComparer.Ordinal);
        var changed = false;
        var inCueBody = false;

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (TimingLine.IsMatch(line))
            {
                inCueBody = true;
                continue;
            }

            if (line.Length == 0)
            {
                inCueBody = false;
                continue;
            }

            if (!inCueBody || IsCueIndex(line, lines, index))
            {
                continue;
            }

            var transformed = TransformLine(line, normalizedPolicy, applied);
            if (!string.Equals(line, transformed, StringComparison.Ordinal))
            {
                lines[index] = transformed;
                changed = true;
            }
        }

        if (!changed)
        {
            return new SubtitleContentModificationResult(content, [], false);
        }

        var rewritten = string.Join("\r\n", lines);
        return new SubtitleContentModificationResult(
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(rewritten),
            applied.OrderBy(rule => rule, StringComparer.Ordinal).ToArray(),
            true);
    }

    private static string TransformLine(
        string line,
        SubtitleContentModificationPolicy policy,
        ISet<string> applied)
    {
        var transformed = line;

        if (policy.StripHearingImpairedAnnotations)
        {
            transformed = HearingImpairedAnnotation.Replace(
                transformed,
                match => IsHearingImpairedAnnotation(match.Groups["body"].Value)
                    ? AddRuleAndRemove(match, "hearing-impaired annotations", applied)
                    : match.Value);
        }

        if (policy.RemoveStyleTags)
        {
            transformed = StyleTag.Replace(
                transformed,
                _ => AddRuleAndRemove("style tags", applied));
        }

        if (policy.RemoveEmoji)
        {
            transformed = RemoveEmoji(transformed, applied);
        }

        if (policy.NormalizeWhitespace)
        {
            var normalized = Whitespace.Replace(transformed.Trim(), " ");
            if (!string.Equals(normalized, transformed, StringComparison.Ordinal))
            {
                applied.Add("whitespace");
                transformed = normalized;
            }
        }

        if (policy.FixAllUppercase)
        {
            var sentenceCase = FixAllUppercase(transformed);
            if (!string.Equals(sentenceCase, transformed, StringComparison.Ordinal))
            {
                applied.Add("all-uppercase text");
                transformed = sentenceCase;
            }
        }

        if (policy.FixOcrErrors)
        {
            var withLetters = OcrStandaloneLForI.Replace(transformed, "I");
            var repaired = OcrDigitInsideWord.Replace(
                withLetters,
                match => ReplaceDigitInsideWord(match, withLetters));
            if (!string.Equals(repaired, transformed, StringComparison.Ordinal))
            {
                applied.Add("OCR errors");
                transformed = repaired;
            }
        }

        if (policy.ReverseRightToLeftPunctuation && RightToLeftCharacter.IsMatch(transformed))
        {
            var match = TrailingRightToLeftPunctuation.Match(transformed.TrimEnd());
            if (match.Success && match.Groups["body"].Value.Length > 0)
            {
                transformed = match.Groups["punct"].Value + match.Groups["body"].Value;
                applied.Add("right-to-left punctuation");
            }
        }

        // Colour last, so the tag wraps the finished line instead of being
        // stripped by a style-tag pass running after it.
        if (!string.IsNullOrWhiteSpace(policy.CueColour) && transformed.Trim().Length > 0)
        {
            transformed = $"<font color=\"{policy.CueColour}\">{transformed}</font>";
            applied.Add("cue colour");
        }

        return transformed;
    }

    /// <summary>
    /// Zero becomes O and one becomes l, cased to match the word around it.
    ///
    /// <para>Uppercase only when both neighbours are uppercase: "N0body" is
    /// "Nobody" rather than "NObody", because the capital there belongs to the
    /// start of the sentence and not to the letter being repaired.</para>
    /// </summary>
    private static string ReplaceDigitInsideWord(Match match, string line)
    {
        var letter = match.Groups["digit"].Value == "0" ? 'o' : 'l';
        var previous = match.Index > 0 ? line[match.Index - 1] : ' ';
        var next = match.Index + 1 < line.Length ? line[match.Index + 1] : ' ';
        return char.IsUpper(previous) && char.IsUpper(next)
            ? char.ToUpperInvariant(letter).ToString()
            : letter.ToString();
    }

    private static string AddRuleAndRemove(Match match, string rule, ISet<string> applied)
    {
        applied.Add(rule);
        return string.Empty;
    }

    private static string AddRuleAndRemove(string rule, ISet<string> applied)
    {
        applied.Add(rule);
        return string.Empty;
    }

    private static bool IsHearingImpairedAnnotation(string body)
        => SoundAnnotation.IsMatch(body);

    private static string RemoveEmoji(string value, ISet<string> applied)
    {
        var builder = new StringBuilder(value.Length);
        var removed = false;
        foreach (var rune in value.EnumerateRunes())
        {
            if (IsEmoji(rune.Value))
            {
                removed = true;
                continue;
            }

            builder.Append(rune.ToString());
        }

        if (removed)
        {
            applied.Add("emoji");
        }

        return removed ? builder.ToString() : value;
    }

    private static bool IsEmoji(int value)
        => value is (>= 0x1F000 and <= 0x1FAFF)
            or (>= 0x2600 and <= 0x27BF)
            or (>= 0x2300 and <= 0x23FF)
            or 0x200D
            or 0x20E3
            or (>= 0xFE00 and <= 0xFE0F);

    private static string FixAllUppercase(string value)
    {
        var letters = value.Where(char.IsLetter).ToArray();
        if (letters.Length < 6 || letters.Any(char.IsLower))
        {
            return value;
        }

        var lower = value.ToLowerInvariant().ToCharArray();
        for (var index = 0; index < lower.Length; index++)
        {
            if (char.IsLetter(lower[index]))
            {
                lower[index] = char.ToUpperInvariant(lower[index]);
                break;
            }
        }

        return new string(lower);
    }

    private static bool IsCueIndex(string line, IReadOnlyList<string> lines, int index)
        => line.All(char.IsAsciiDigit)
            && index + 1 < lines.Count
            && TimingLine.IsMatch(lines[index + 1]);

    private static string Decode(byte[] content)
    {
        if (content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(content, 3, content.Length - 3);
        }

        return Encoding.UTF8.GetString(content);
    }
}

public sealed record SubtitleContentModificationResult(
    byte[] Content,
    IReadOnlyList<string> AppliedRules,
    bool Modified);
