using System.Reflection;
using System.Text.RegularExpressions;

namespace Deluno.Api.Updates;

/// <summary>
/// Parses the repo's plain-language release notes (<c>RELEASE_NOTES.md</c>,
/// one <c>## &lt;version&gt;</c> section per version) into a lookup by
/// version. Used to attach each applied update's own notes on the Updates
/// card (#870).
/// </summary>
public static partial class ReleaseNotesParser
{
    [GeneratedRegex(@"^##\s+(?<version>\S+)\s*$", RegexOptions.Multiline)]
    private static partial Regex VersionHeadingPattern();

    /// <summary>
    /// Splits the document into version -> body sections. A "version" is
    /// whatever follows "## " up to the end of the line, matched verbatim
    /// against the version string callers look up (e.g. "1.0.0-rc.22"); a
    /// leading "v" is not assumed or stripped. The body is the heading's
    /// text up to (not including) the next "## " heading, trimmed.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Parse(string markdown)
    {
        var sections = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return sections;
        }

        // The embedded file keeps whatever line endings the checkout gave it
        // (CRLF on Windows, LF on Linux); notes read the same either way.
        markdown = markdown.Replace("\r\n", "\n").Replace('\r', '\n');

        var matches = VersionHeadingPattern().Matches(markdown);
        for (var i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            var version = match.Groups["version"].Value.Trim();
            var bodyStart = match.Index + match.Length;
            var bodyEnd = i + 1 < matches.Count ? matches[i + 1].Index : markdown.Length;
            var body = markdown[bodyStart..bodyEnd].Trim();

            // A version repeated in the file (should not happen, but a typo'd
            // duplicate heading should not silently win over the real one) -
            // keep the first occurrence.
            sections.TryAdd(version, body);
        }

        return sections;
    }
}

/// <summary>
/// Looks up the plain-language notes for a version, so the Updates card can
/// show what an applied (or, once an update checker exists, a pending)
/// version actually changed.
/// </summary>
public interface IReleaseNotesProvider
{
    /// <summary>Returns the notes body for a version, or null when the notes file has no section for it.</summary>
    string? GetNotes(string version);
}

/// <summary>
/// Reads RELEASE_NOTES.md from the assembly's embedded resources (see the
/// EmbeddedResource entry in Deluno.Api.csproj). Parsed once and cached: the
/// file ships baked into the build, so it cannot change without a redeploy.
/// </summary>
public sealed class EmbeddedReleaseNotesProvider : IReleaseNotesProvider
{
    private const string ResourceName = "Deluno.Api.RELEASE_NOTES.md";

    private readonly Lazy<IReadOnlyDictionary<string, string>> _sections;

    public EmbeddedReleaseNotesProvider()
    {
        _sections = new Lazy<IReadOnlyDictionary<string, string>>(LoadSections);
    }

    public string? GetNotes(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        return _sections.Value.TryGetValue(version, out var notes) ? notes : null;
    }

    private static IReadOnlyDictionary<string, string> LoadSections()
    {
        var assembly = typeof(EmbeddedReleaseNotesProvider).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        using var reader = new StreamReader(stream);
        var markdown = reader.ReadToEnd();
        return ReleaseNotesParser.Parse(markdown);
    }
}
