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
}
