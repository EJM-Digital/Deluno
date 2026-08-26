namespace Deluno.Recovery.Policies;

/// <summary>
/// Whether a download that is still being shared is actually costing the user
/// any disk, and how to say so in plain words (#288).
///
/// After an import a title exists twice — once in the library, once in the
/// download client. Whether that is two copies or one depends entirely on
/// arrangement: on the same drive, with single-copy links in use, both names
/// point at one set of file data and sharing is free. On different drives it
/// cannot be, and every shared download is holding its own gigabytes.
///
/// Deluno has to know which, because otherwise the dashboard either frightens
/// people who are losing nothing or quietly lets a drive fill.
/// </summary>
public static class SharingFootprint
{
    /// <summary>
    /// True when the download client's copy and the library's are one set of
    /// file data. Unknown paths answer false: a guess that understates disk use
    /// is the one that lets a drive fill up silently.
    /// </summary>
    public static bool SharesOneCopy(string? downloadPath, string? libraryPath, bool useHardlinks)
        => useHardlinks && OnSameDrive(downloadPath, libraryPath) == true;

    /// <summary>
    /// What to tell someone about where their two copies live — and only when
    /// there is something to tell. Null means the arrangement is already the
    /// good one, or that Deluno cannot see enough to make a claim, and in both
    /// cases inventing a sentence would be noise.
    /// </summary>
    public static string? Describe(string? downloadPath, string? libraryPath, bool useHardlinks)
    {
        var sameDrive = OnSameDrive(downloadPath, libraryPath);
        if (sameDrive is null)
        {
            return null;
        }

        if (sameDrive == false)
        {
            var downloadDrive = DescribeDrive(downloadPath);
            var libraryDrive = DescribeDrive(libraryPath);
            return $"Your downloads land on {downloadDrive} and your library is on {libraryDrive}, so each one Deluno is still sharing takes its own space.";
        }

        return useHardlinks
            ? null
            : "These are on the same drive as your library, so sharing could cost nothing — turn on \"Keep seeding without a second copy\" under Media Management.";
    }

    /// <summary>
    /// Null when either path is missing or unreadable — which is a different
    /// answer from "no", and the caller has to be able to tell them apart.
    /// </summary>
    private static bool? OnSameDrive(string? first, string? second)
    {
        var firstRoot = RootOf(first);
        var secondRoot = RootOf(second);
        if (firstRoot is null || secondRoot is null)
        {
            return null;
        }

        return string.Equals(firstRoot, secondRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string? RootOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrWhiteSpace(root) ? null : root;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return null;
        }
    }

    /// <summary>"C:" rather than "C:\" — how a person says it.</summary>
    private static string DescribeDrive(string? path)
    {
        var root = RootOf(path);
        if (root is null)
        {
            return "another drive";
        }

        var trimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.IsNullOrWhiteSpace(trimmed) ? root : trimmed;
    }
}
