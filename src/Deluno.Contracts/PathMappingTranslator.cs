namespace Deluno.Contracts;

/// <summary>
/// The one longest-prefix path mapping match used everywhere Deluno
/// translates a path reported from one filesystem namespace into another:
/// download clients in a container or on another machine (#809), and
/// processor connections in the same position (#962). The longest matching
/// root wins so a specific mapping can safely sit beside a broader one; ties
/// are broken by the mapping's own priority. With no mappings, or none that
/// match, the path passes through unchanged. A path is normalised before and
/// after mapping (<see cref="SafePath"/>): one that climbs out of its source
/// root, or whose mapped result would land outside the target root, is refused
/// (<c>Translate</c> returns null, <c>TryTranslate</c> says why) and never used.
/// </summary>
public static class PathMappingTranslator
{
    public static string? Translate<TMapping>(
        string? reportedPath,
        IEnumerable<TMapping> mappings,
        Func<TMapping, bool> isEnabled,
        Func<TMapping, string> sourceRoot,
        Func<TMapping, string> targetRoot,
        Func<TMapping, int> priority)
        => TryTranslate(reportedPath, mappings, isEnabled, sourceRoot, targetRoot, priority, out _);

    /// <summary>As <c>Translate</c>, with the plain reason a path was refused (null when it was not).</summary>
    public static string? TryTranslate<TMapping>(
        string? reportedPath,
        IEnumerable<TMapping> mappings,
        Func<TMapping, bool> isEnabled,
        Func<TMapping, string> sourceRoot,
        Func<TMapping, string> targetRoot,
        Func<TMapping, int> priority,
        out string? refusedReason)
    {
        refusedReason = null;
        if (string.IsNullOrWhiteSpace(reportedPath))
        {
            return reportedPath;
        }

        var normalizedReported = NormalizeKey(reportedPath);
        var match = mappings
            .Where(isEnabled)
            .Select(item => new { Mapping = item, Source = NormalizeKey(sourceRoot(item)) })
            .Where(item => normalizedReported.Equals(item.Source, StringComparison.OrdinalIgnoreCase) ||
                           normalizedReported.StartsWith(item.Source + "/", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Source.Length)
            .ThenBy(item => priority(item.Mapping))
            .FirstOrDefault();

        if (match is null)
        {
            return reportedPath;
        }

        var target = targetRoot(match.Mapping);
        var relative = normalizedReported[match.Source.Length..].TrimStart('/');
        if (relative.Length == 0)
        {
            return target;
        }

        // Resolved lexically against the target root: a ".." that climbs out of the source root, or a result
        // that lands outside the target root, is refused rather than handed to the file system.
        if (!SafePath.TryResolveUnder(target, relative, out var mapped, out var reason))
        {
            refusedReason = reason;
            return null;
        }

        // Same spelling of the target root and platform separators as before, over the now-clean segments.
        var cleanRelative = mapped[(SafePath.Normalize(target)!.TrimEnd('/').Length)..];
        return cleanRelative.Split('/', StringSplitOptions.RemoveEmptyEntries).Aggregate(target, Path.Combine);
    }

    public static string NormalizeKey(string value)
        => value.Trim().TrimEnd('\\', '/').Replace('\\', '/');
}
