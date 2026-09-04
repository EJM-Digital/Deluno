namespace Deluno.Filesystem;

/// <summary>
/// Moving around the filesystem in the folder picker.
///
/// <para>Its own type so the rule can be tested without starting a web host —
/// which is the reason it had no test at all, and the reason the defect below
/// reached an installed build.</para>
/// </summary>
public static class DirectoryNavigation
{
    /// <summary>
    /// The directory above this one, or <c>null</c> when there is nowhere above
    /// to go.
    ///
    /// <para>The browse endpoint appends a trailing separator on Windows so a
    /// drive root renders as <c>C:\</c> rather than <c>C:</c>.
    /// <see cref="Directory.GetParent(string)"/> reads that separator as "this
    /// path is the directory" and answers with the same folder minus the
    /// separator — so browsing to <c>C:\Media\Films\</c> reported its parent as
    /// <c>C:\Media\Films</c>. The picker could descend and never climb:
    /// pressing up landed you where you already were, and typing a path was the
    /// only way out.</para>
    ///
    /// <para>A drive root trims to <c>C:</c>, which has no parent worth
    /// offering — <c>null</c> tells the caller to show the drive list
    /// instead.</para>
    /// </summary>
    public static string? ParentOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (OperatingSystem.IsWindows() && trimmed.Length == 2 && trimmed[1] == ':')
        {
            return null;
        }

        return Directory.GetParent(trimmed)?.FullName;
    }
}
