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

    /// <summary>
    /// Which volume a path is on — which is not what <see cref="Path.GetPathRoot(string?)"/>
    /// answers, on either host.
    ///
    /// <para><b>A Windows path read by Linux.</b> <c>Path</c> answers for the
    /// machine it runs on, so on Linux <c>C:\Deluno</c> is a relative path and
    /// <c>GetFullPath</c> makes it <c>/somewhere/C:\Deluno</c> with root
    /// <c>/</c>. Two different drives then compared equal, and this reported
    /// that a download on <c>C:</c> and a library on <c>D:</c> were one set of
    /// file data. Deluno stores these paths, and the machine reading one back
    /// is not always the one that wrote it, so a drive letter is recognised by
    /// its shape.</para>
    ///
    /// <para><b>A known gap, left open deliberately.</b> On a genuinely POSIX
    /// install every path is rooted at <c>/</c>, so this still cannot tell two
    /// volumes apart there — and in the container image <c>/downloads</c> and
    /// <c>/media</c> being separate mounts is the normal arrangement, which a
    /// hardlink cannot cross. Answering that needs the mount table, and reading
    /// it here is what broke: <see cref="DriveInfo.GetDrives"/> stats every
    /// mount, a CI runner has some that do not answer promptly, and a test run
    /// that had passed sat for eight minutes without exiting. Caching the
    /// reading fixed the cost and not the blocking. It is worth doing on a
    /// machine where it can be measured, which is not here, so it is not being
    /// guessed at here.</para>
    /// </summary>
    private static string? RootOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (WindowsRootOf(path) is { } windowsRoot)
        {
            return windowsRoot;
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

    /// <summary>
    /// <c>C:\</c> from <c>C:\Deluno\Movies</c>, or <c>\\server\share</c> from a
    /// UNC path — read from the path's shape, so the answer does not change
    /// with the host. Null when the path is not Windows-shaped.
    /// </summary>
    internal static string? WindowsRootOf(string path)
    {
        if (path.Length >= 2 && path[1] == ':' && char.IsLetter(path[0]))
        {
            return string.Concat(char.ToUpperInvariant(path[0]), ":\\");
        }

        // Backslashes only. A UNC path written by Windows uses them, and a
        // POSIX path is allowed to begin with "//" — reading that as a share
        // would invent a volume out of a leading slash.
        if (path.Length > 2 && path[0] == '\\' && path[1] == '\\')
        {
            // A share is the volume: \\server\share. Anything deeper is a
            // folder on it.
            var parts = path[2..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 ? $@"\\{parts[0]}\{parts[1]}" : null;
        }

        return null;
    }


    /// <summary>"C:" rather than "C:\" — how a person says it.</summary>
    private static string DescribeDrive(string? path)
    {
        var root = RootOf(path);
        if (root is null)
        {
            return "another drive";
        }

        // Both separators, not the host's: this root may be a Windows one that
        // a Linux container is reading back.
        var trimmed = root.TrimEnd('/', '\\');
        return string.IsNullOrWhiteSpace(trimmed) ? root : trimmed;
    }
}
