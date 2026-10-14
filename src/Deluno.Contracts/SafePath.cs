namespace Deluno.Contracts;

/// <summary>
/// The one place Deluno decides whether a path that came from outside (a download client's save path, a
/// processor's output, an import folder, a backup entry, a tool's file-changed call) stays inside the folder
/// it is meant to live in. The work is lexical, so it reads the same on Windows and Linux and for paths written
/// on another machine: both separators count, repeated separators collapse, <c>.</c> is dropped and <c>..</c>
/// removes the segment before it. A path that climbs above its own start cannot be normalised and is refused.
/// </summary>
public static class SafePath
{
    /// <summary>The plain reason shown when a path would land outside the folder it belongs in.</summary>
    public const string OutsideRootReason = "That path climbs out of the folder it belongs in, so Deluno will not use it.";

    /// <summary>
    /// The path with <c>/</c> separators and no <c>.</c>, <c>..</c> or repeated separators, or null when it climbs
    /// above its own start (or is empty). A leading double separator (a network share) is kept.
    /// </summary>
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var text = path.Trim().Replace('\\', '/');
        var unc = text.StartsWith("//", StringComparison.Ordinal);
        var rooted = text.StartsWith('/');
        var parts = new List<string>();
        foreach (var segment in text.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                // A drive ("D:") or the first segment of a share is a root, not something to climb out of.
                var floor = unc ? 2 : (parts.Count > 0 && parts[0].EndsWith(':') ? 1 : 0);
                if (parts.Count <= floor)
                {
                    return null;
                }

                parts.RemoveAt(parts.Count - 1);
                continue;
            }

            parts.Add(segment);
        }

        var joined = string.Join('/', parts);
        return unc ? "//" + joined : rooted ? "/" + joined : joined;
    }

    /// <summary>
    /// True when the path is a full path (a drive, a share or a leading separator) with no <c>.</c> or <c>..</c>
    /// segment, for a caller that must use the text as given (it is matched against stored paths).
    /// </summary>
    public static bool IsPlainFullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var text = path.Trim().Replace('\\', '/');
        var rooted = text.StartsWith('/') || (text.Length > 1 && text[1] == ':');
        return rooted && !text.Split('/').Any(segment => segment is "." or "..");
    }

    /// <summary>True when <paramref name="path"/> is below <paramref name="root"/> (or is the root, when allowed), after both are normalised.</summary>
    public static bool IsInside(string? root, string? path, bool allowEqual = false, StringComparison? comparison = null)
    {
        var normalRoot = Normalize(root)?.TrimEnd('/');
        var normalPath = Normalize(path)?.TrimEnd('/');
        if (normalRoot is null || normalPath is null)
        {
            return false;
        }

        var compare = comparison ?? (OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        if (normalPath.Equals(normalRoot, compare))
        {
            return allowEqual;
        }

        return normalPath.StartsWith(normalRoot + "/", compare);
    }

    /// <summary>
    /// Joins a path that came from outside onto <paramref name="root"/>. Refuses (false, with a plain reason) when
    /// the part is rooted or climbs out, so the result is always below the root.
    /// </summary>
    public static bool TryResolveUnder(string? root, string? relative, out string result, out string reason)
    {
        result = string.Empty;
        reason = OutsideRootReason;
        var normalRoot = Normalize(root);
        if (normalRoot is null)
        {
            reason = "There is no folder to put that path under.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(relative))
        {
            result = normalRoot;
            return true;
        }

        var text = relative.Trim().Replace('\\', '/');
        if (text.StartsWith('/') || (text.Length > 1 && text[1] == ':'))
        {
            return false;
        }

        var joined = Normalize(normalRoot.TrimEnd('/') + "/" + text);
        if (joined is null || !IsInside(normalRoot, joined, allowEqual: true, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        result = joined;
        reason = string.Empty;
        return true;
    }
}
