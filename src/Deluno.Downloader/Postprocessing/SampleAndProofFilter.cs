using System.Text.RegularExpressions;

namespace Deluno.Downloader.Postprocessing;

/// <summary>
/// Removes files matching release-group "sample" / "proof" / "screens"
/// conventions. These are short preview clips, NFOs, and screenshot
/// directories shipped alongside the real payload — never wanted by the
/// importer.
///
/// Patterns are configurable but ship with a sensible default. Files are
/// deleted from disk and excluded from the returned set.
/// </summary>
public sealed class SampleAndProofFilter : IPostProcessor
{
    private static readonly Regex[] DefaultPatterns =
    [
        new(@"(^|[\\/])sample([\\/]|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"[\\/][^\\/]*sample[^\\/]*\.[a-z0-9]{2,4}$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"(^|[\\/])proof([\\/]|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"(^|[\\/])screens?([\\/]|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\.nfo$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\.sfv$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\.url$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    ];

    private readonly IReadOnlyList<Regex> _patterns;

    public SampleAndProofFilter(IReadOnlyList<Regex>? patternsOverride = null)
        => _patterns = patternsOverride ?? DefaultPatterns;

    public Task<IReadOnlyList<string>> ProcessAsync(
        string workingDir,
        IReadOnlyList<string> files,
        CancellationToken ct)
    {
        var kept = new List<string>(files.Count);
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            if (_patterns.Any(p => p.IsMatch(f)))
            {
                try { File.Delete(f); } catch { /* best-effort */ }
            }
            else
            {
                kept.Add(f);
            }
        }
        return Task.FromResult<IReadOnlyList<string>>(kept);
    }
}
