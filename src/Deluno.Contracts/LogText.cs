namespace Deluno.Contracts;

/// <summary>
/// The one place Deluno makes text that came from outside (a release name, a path a client reported, a search
/// query, a request header) safe to write into a log line. A log is read line by line, so a value that carries a
/// line break could add a line that looks like Deluno wrote it. Every log call that names such a value passes it
/// through <see cref="ForLog"/>.
/// </summary>
public static class LogText
{
    /// <summary>
    /// The text with every control character (line breaks, tabs, escapes) turned into a space and the line break
    /// characters taken out, or the same null or empty value that came in.
    /// </summary>
    public static string? ForLog(this string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var characters = value.ToCharArray();
        for (var index = 0; index < characters.Length; index++)
        {
            if (char.IsControl(characters[index]))
            {
                characters[index] = ' ';
            }
        }

        // U+2028 and U+2029 are line breaks to some log viewers although they are not control characters.
        return new string(characters).Replace("\r", " ").Replace("\n", " ").Replace("\u2028", " ").Replace("\u2029", " ");
    }

    /// <summary>The same for a value that is not text yet (a path, a URI, an exception message source): its text form, cleaned.</summary>
    public static string? ForLog(this object? value) => value is string text ? text.ForLog() : value?.ToString().ForLog();
}
