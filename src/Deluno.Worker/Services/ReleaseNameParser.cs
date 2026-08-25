namespace Deluno.Worker.Services;

/// <summary>
/// Facts Deluno can infer from a release name when nothing better is known.
/// Only a fallback: an import whose grab is tied to a catalogue item is named
/// from that item, because a release name is the uploader's text, not data.
/// </summary>
public static class ReleaseNameParser
{
    private static readonly char[] TokenSeparators = [' ', '.', '-', '_', '[', ']', '(', ')'];

    /// <summary>
    /// The release year in a release name, or null when there is none.
    /// </summary>
    /// <remarks>
    /// Takes the <em>last</em> year-like token rather than the first. A title
    /// can contain a number that reads as a year, and it always precedes the
    /// real one: "Blade.Runner.2049.2017.1080p" is the 2017 release of a film
    /// called Blade Runner 2049, and "2001.A.Space.Odyssey.1968" is 1968.
    /// Taking the first token named the imported folder "(2049)" (#268).
    /// </remarks>
    public static int? InferYear(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        int? inferred = null;
        foreach (var part in value.Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Length == 4 &&
                int.TryParse(part, out var year) &&
                year is >= 1900 and <= 2100)
            {
                inferred = year;
            }
        }

        return inferred;
    }
}
