using Deluno.Platform.Contracts;

namespace Deluno.Platform;

/// <summary>
/// The one place that decides what an illegal filename character becomes.
///
/// <para>Naming render (<see cref="NamingTemplateRenderer"/>) and the import
/// file-naming path (<c>ImportPipelineService.SanitizeFileName</c>) each used
/// to carry their own copy of this decision. Two copies of a character rule
/// is exactly the shape the product's own standing check calls out: "every
/// defect worth finding in this codebase so far has been one rule written
/// twice in two places that could not check each other." #757 and #759 gave
/// the rule two settings to obey, which is the point at which the second copy
/// would have drifted.</para>
/// </summary>
public static class FileNameSanitizer
{
    /// <summary>
    /// Turns whatever a title, token or template segment contains into
    /// something safe to put on disk, honouring #757's on/off switch and
    /// #759's colon mode.
    /// </summary>
    /// <param name="replaceIllegalCharacters">
    /// #757: on (the historical default) turns an illegal character into a
    /// dash; off removes it instead -- colon included, regardless of
    /// <paramref name="colonReplacementMode"/>.
    /// </param>
    /// <param name="colonReplacementMode">
    /// #759: one of <see cref="ColonReplacementModes"/>. Only consulted when
    /// <paramref name="replaceIllegalCharacters"/> is true; every other
    /// illegal character keeps the fixed dash-or-remove behaviour.
    /// </param>
    public static string Sanitize(
        string? value,
        bool replaceIllegalCharacters = true,
        string colonReplacementMode = ColonReplacementModes.SmartReplace)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        // Both separators, always, whoever is running.
        //
        // Path.GetInvalidFileNameChars() answers for the host: on Windows it
        // returns the whole set, on Linux it returns exactly NUL and '/'. The
        // characters after it were already spelled out to cover that gap - and
        // the backslash was not among them. So on Linux a title containing one
        // survived, and RenderFolder then split on it: "Title\with separators"
        // became "Title/with separators", a nested folder built out of a title,
        // which is the one thing this function exists to prevent. The container
        // image is the Linux one, and a library it writes is read by Windows
        // clients over a share.
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == ':')
            {
                if (!replaceIllegalCharacters)
                {
                    continue;
                }

                var nextIsSpace = index + 1 < value.Length && value[index + 1] == ' ';
                switch (ColonReplacementModes.Normalize(colonReplacementMode))
                {
                    case ColonReplacementModes.Delete:
                        break;
                    case ColonReplacementModes.Dash:
                        builder.Append('-');
                        break;
                    case ColonReplacementModes.SpaceDash:
                        builder.Append(" -");
                        break;
                    case ColonReplacementModes.SpaceDashSpace:
                        builder.Append(" - ");
                        break;
                    default: // SmartReplace
                        if (nextIsSpace)
                        {
                            builder.Append(" - ");
                            index++; // the space belongs to the replacement, not the text after it.
                        }
                        else
                        {
                            builder.Append('-');
                        }
                        break;
                }

                continue;
            }

            // A control character (a line break from a pasted description, a stray
            // backspace) is illegal on every platform. Linux's own invalid set is
            // only NUL and '/', so the host list alone lets it through.
            if (char.IsControl(character))
            {
                if (replaceIllegalCharacters)
                {
                    builder.Append(' ');
                }

                continue;
            }

            var isIllegal = invalid.Contains(character) ||
                character is '/' or '\\' or '<' or '>' or '"' or '|' or '?' or '*';
            if (!isIllegal)
            {
                builder.Append(character);
            }
            else if (replaceIllegalCharacters)
            {
                builder.Append('-');
            }
        }

        return builder.ToString().Trim();
    }

    /// <summary>The longest a single file or folder name may be, in UTF-8 bytes, on the common file systems.</summary>
    public const int MaxNameBytes = 255;

    /// <summary>
    /// Windows drops a dot or space that ends a whole name, so the name Deluno records would
    /// not be the name on disk. Callers trim only the end of the finished name (before the
    /// extension), never inside it: "Mr. (2001)" is a good name.
    /// </summary>
    public static string TrimEnd(string value) => value.TrimEnd('.', ' ');

    public static int ByteCount(string value) => System.Text.Encoding.UTF8.GetByteCount(value);

    /// <summary>
    /// Cuts <paramref name="value"/> to at most <paramref name="maxBytes"/> UTF-8
    /// bytes at a character boundary (a surrogate pair is never split) and
    /// removes a trailing space the cut leaves.
    /// </summary>
    public static string LimitBytes(string value, int maxBytes)
    {
        if (maxBytes <= 0)
        {
            return string.Empty;
        }

        if (ByteCount(value) <= maxBytes)
        {
            return value;
        }

        var bytes = 0;
        var end = 0;
        while (end < value.Length)
        {
            var width = char.IsHighSurrogate(value[end]) && end + 1 < value.Length && char.IsLowSurrogate(value[end + 1]) ? 2 : 1;
            var size = System.Text.Encoding.UTF8.GetByteCount(value.AsSpan(end, width));
            if (bytes + size > maxBytes)
            {
                break;
            }

            bytes += size;
            end += width;
        }

        return value[..end].TrimEnd();
    }
}
