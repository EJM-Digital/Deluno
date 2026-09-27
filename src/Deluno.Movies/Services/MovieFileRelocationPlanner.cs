namespace Deluno.Movies.Services;

/// <summary>
/// Where a movie's file lands when a bulk library reassignment chooses to
/// move it (#787), decided without touching disk so the decision itself can
/// be tested without a filesystem.
/// </summary>
/// <param name="SourceDirectory">The folder the file currently sits in.</param>
/// <param name="DestinationDirectory">The folder it moves into.</param>
/// <param name="MovesWholeFolder">
/// True when the file's whole containing folder relocates - the normal case,
/// which keeps extras and subtitles beside it rather than orphaning them
/// under the old root. False only when the file was not actually under the
/// source library's own root (a manually-relocated path), where there is no
/// folder this planner can safely claim belongs to the title, so only the
/// file itself moves, straight into the destination root.
/// </param>
public sealed record MovieFileRelocationPlan(
    string SourceDirectory,
    string DestinationDirectory,
    bool MovesWholeFolder);

public static class MovieFileRelocationPlanner
{
    /// <summary>
    /// Returns null when <paramref name="filePath"/> has no containing
    /// directory at all (a bare filename), which nothing here can plan a
    /// folder move for.
    /// </summary>
    public static MovieFileRelocationPlan? Plan(string filePath, string fromLibraryRootPath, string toLibraryRootPath)
    {
        var sourceDirectory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(sourceDirectory))
        {
            return null;
        }

        var relativeDirectory = Path.GetRelativePath(fromLibraryRootPath, sourceDirectory);
        var movesWholeFolder = !relativeDirectory.StartsWith("..", StringComparison.Ordinal)
            && !Path.IsPathRooted(relativeDirectory);
        var destinationDirectory = movesWholeFolder
            ? Path.Combine(toLibraryRootPath, relativeDirectory)
            : toLibraryRootPath;

        return new MovieFileRelocationPlan(sourceDirectory, destinationDirectory, movesWholeFolder);
    }
}
