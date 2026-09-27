namespace Deluno.Series.Services;

/// <summary>
/// Where a series' folder lands when a bulk rename is applied (#788),
/// decided without touching disk so it can be tested without a filesystem.
///
/// A series has many episode files, not one, so there is no single stored
/// "series folder" to rename - only file paths. This finds the deepest folder
/// every one of a series' episode files sits under (whatever the season
/// layout - "Series/Season 01/ep.mkv", or files sitting flat with no season
/// folder at all) and renames only that, which is exactly the folder the
/// series-level naming template describes.
/// </summary>
public sealed record SeriesFolderRelocationPlan(
    string SourceDirectory,
    string DestinationDirectory);

public static class SeriesFolderRelocationPlanner
{
    /// <summary>
    /// Returns null when there is nothing to plan: no files, or the files do
    /// not share any folder at all (which should not happen for one series,
    /// but a caller that cannot prove a folder belongs to the title must not
    /// guess one).
    /// </summary>
    public static SeriesFolderRelocationPlan? Plan(IReadOnlyList<string> episodeFilePaths, string proposedFolderName)
    {
        if (episodeFilePaths.Count == 0 || string.IsNullOrWhiteSpace(proposedFolderName))
        {
            return null;
        }

        var directories = episodeFilePaths
            .Select(Path.GetDirectoryName)
            .Where(directory => !string.IsNullOrEmpty(directory))
            .Select(directory => directory!)
            .ToArray();

        if (directories.Length != episodeFilePaths.Count)
        {
            // At least one file path had no containing directory at all.
            return null;
        }

        var commonDirectory = FindCommonAncestor(directories);
        if (commonDirectory is null)
        {
            return null;
        }

        var parentDirectory = Path.GetDirectoryName(commonDirectory);
        if (string.IsNullOrEmpty(parentDirectory))
        {
            return null;
        }

        return new SeriesFolderRelocationPlan(commonDirectory, Path.Combine(parentDirectory, proposedFolderName));
    }

    private static string? FindCommonAncestor(IReadOnlyList<string> directories)
    {
        var separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
        var segmented = directories
            .Select(directory => directory.Split(separators, StringSplitOptions.RemoveEmptyEntries))
            .ToArray();

        var isRooted = Path.IsPathRooted(directories[0]);
        var commonLength = segmented[0].Length;
        for (var i = 1; i < segmented.Length && commonLength > 0; i++)
        {
            var matched = 0;
            var max = Math.Min(commonLength, segmented[i].Length);
            while (matched < max
                   && string.Equals(segmented[0][matched], segmented[i][matched], StringComparison.OrdinalIgnoreCase))
            {
                matched++;
            }

            commonLength = matched;
        }

        if (commonLength == 0)
        {
            return null;
        }

        var joined = string.Join(Path.DirectorySeparatorChar, segmented[0].Take(commonLength));
        // A POSIX absolute path loses its leading separator when split on it;
        // a Windows one keeps its drive letter as the first segment (`C:`)
        // and needs nothing extra.
        return isRooted && !Path.IsPathRooted(joined) ? Path.DirectorySeparatorChar + joined : joined;
    }
}
