namespace Deluno.Contracts;

/// <summary>
/// The one longest-prefix path mapping match used everywhere Deluno
/// translates a path reported from one filesystem namespace into another:
/// download clients in a container or on another machine (#809), and
/// processor connections in the same position (#962). The longest matching
/// root wins so a specific mapping can safely sit beside a broader one; ties
/// are broken by the mapping's own priority. With no mappings, or none that
/// match, the path passes through unchanged.
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
    {
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

        var segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Aggregate(target, Path.Combine);
    }

    public static string NormalizeKey(string value)
        => value.Trim().TrimEnd('\\', '/').Replace('\\', '/');
}
