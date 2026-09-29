namespace Deluno.Movies.Services;

/// <summary>
/// Where a movie's file lands when a bulk library reassignment chooses to
/// move it (#787), decided without touching disk so the decision itself can
/// be tested without a filesystem.
/// </summary>
/// <param name="SourceDirectory">The folder the file currently sits in.</param>
/// <param name="DestinationDirectory">The folder it moves into.</param>
/// <param name="MovesWholeFolder">
/// True when the movie's own folder relocates - which keeps extras and
/// subtitles beside the file. False when the file sat straight in the source
/// library folder (#1078): then only that one file moves, into a new title
/// folder in the destination library.
/// </param>
public sealed record MovieFileRelocationPlan(
    string SourceDirectory,
    string DestinationDirectory,
    bool MovesWholeFolder);

/// <summary>What the planner decided: a plan to carry out, or a plain-words reason nothing moves.</summary>
public sealed record MovieFileRelocationOutcome(MovieFileRelocationPlan? Plan, string? RefusalReason)
{
    public bool IsRefused => Plan is null;
}

public static class MovieFileRelocationPlanner
{
    /// <summary>
    /// Decides what moves when a movie is reassigned with "move files" (#1078).
    /// Only ever the title's own folder (proven by the shared
    /// <see cref="Deluno.Platform.TitleFolderGuard"/>), or - for a file sitting
    /// straight in the source library folder - that single file, into a new
    /// title folder in the destination library named by
    /// <paramref name="flatFileFolderName"/>. Anything else is refused with a
    /// reason: a folder holding other titles' files, a library or drive root,
    /// a path outside the libraries. Nothing here touches disk; the apply
    /// calls this at the moment of the move, so it re-checks.
    /// </summary>
    public static async Task<MovieFileRelocationOutcome> PlanAsync(
        string filePath,
        string fromLibraryRootPath,
        string toLibraryRootPath,
        IReadOnlyList<string> libraryRoots,
        Func<string, CancellationToken, Task<bool>> otherTitlesHaveFilesUnder,
        string flatFileFolderName,
        CancellationToken cancellationToken)
    {
        var target = await Deluno.Platform.TitleFolderGuard.ForMovieAsync(
            filePath, libraryRoots, otherTitlesHaveFilesUnder, cancellationToken);
        var sourceDirectory = Path.GetDirectoryName(filePath);

        if (target.IsSafe)
        {
            var relative = Path.GetRelativePath(fromLibraryRootPath, target.Folder!);
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            {
                return Refuse("This movie's folder is not inside the library it is being moved from, so Deluno leaves its files where they are.");
            }

            return new MovieFileRelocationOutcome(
                new MovieFileRelocationPlan(target.Folder!, Path.Combine(toLibraryRootPath, relative), MovesWholeFolder: true),
                null);
        }

        // A file filed straight in the source library folder has no folder of
        // its own to move. Move just that file, into a new title folder.
        if (target.Code == "flat-layout"
            && !string.IsNullOrWhiteSpace(flatFileFolderName)
            && sourceDirectory is not null
            && Path.GetRelativePath(fromLibraryRootPath, sourceDirectory) == ".")
        {
            return new MovieFileRelocationOutcome(
                new MovieFileRelocationPlan(
                    sourceDirectory,
                    Path.Combine(toLibraryRootPath, flatFileFolderName),
                    MovesWholeFolder: false),
                null);
        }

        return Refuse(target.Message);
    }

    private static MovieFileRelocationOutcome Refuse(string reason) => new(null, reason);
}
