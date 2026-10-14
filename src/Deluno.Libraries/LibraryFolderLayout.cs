using Deluno.Contracts;
using Deluno.Libraries.Contracts;

namespace Deluno.Libraries;

/// <summary>
/// The folders one library uses, as clean-up sees them.
/// </summary>
public sealed record LibraryFolders(string Id, string Name, string? RootPath, string? DownloadsPath, string? ProcessorOutputPath)
{
    public static LibraryFolders From(LibraryItem library)
        => new(library.Id, library.Name, library.RootPath, library.DownloadsPath, library.ProcessorOutputPath);
}

/// <summary>
/// One place two folders sit on top of each other so that clean-up could reach media (#1325).
/// <see cref="LibraryId"/> is the library whose folder is wrong; <see cref="Message"/> is the plain sentence naming both folders.
/// </summary>
public sealed record LibraryFolderOverlap(string LibraryId, string OtherLibraryId, string Message);

/// <summary>
/// The one folder-layout rule for libraries (#1325): the folders clean-up removes things from (a library's downloads folder, which is
/// also the folder the processor watches, and the processor output folder) may not be, or contain, any library's root, and no library
/// root may be inside another's. A layout that breaks this lets Clear, the leftovers delete and the orphan remover reach a film: a
/// folder that holds a library is a folder that is safe to empty, as far as they can tell.
///
/// <para>Used when a library is created or changed (the Libraries screen, the setup guide, every API), by the health check that raises
/// a problem for an install that already has an overlap, and by every clean-up, which refuses to act while one lasts.</para>
/// </summary>
public static class LibraryFolderLayout
{
    public const string DownloadsRole = "downloads";
    public const string ProcessorOutputRole = "processor output";

    /// <summary>Every overlap in the layout, one per pair of folders.</summary>
    public static IReadOnlyList<LibraryFolderOverlap> FindOverlaps(IEnumerable<LibraryFolders> libraries)
    {
        var all = libraries.ToArray();
        var overlaps = new List<LibraryFolderOverlap>();

        foreach (var library in all)
        {
            foreach (var (role, folder) in WorkFolders(library))
            {
                foreach (var other in all)
                {
                    if (Full(other.RootPath) is not { } root || Full(folder) is not { } fullFolder || !Holds(fullFolder, root))
                    {
                        continue;
                    }

                    var whose = string.Equals(other.Id, library.Id, StringComparison.OrdinalIgnoreCase)
                        ? "its own library folder"
                        : $"the {other.Name} library's folder";
                    overlaps.Add(new LibraryFolderOverlap(
                        library.Id,
                        other.Id,
                        $"The {library.Name} library's {role} folder ({folder}) is, or holds, {whose} ({other.RootPath!.Trim()}). "
                        + "Choose folders that do not sit on top of each other, so clean-up can never reach your media."));
                }
            }
        }

        for (var i = 0; i < all.Length; i++)
        {
            for (var j = i + 1; j < all.Length; j++)
            {
                if (Full(all[i].RootPath) is not { } a || Full(all[j].RootPath) is not { } b)
                {
                    continue;
                }

                if (Holds(a, b) || Holds(b, a))
                {
                    overlaps.Add(new LibraryFolderOverlap(
                        all[i].Id,
                        all[j].Id,
                        $"The {all[i].Name} library's folder ({all[i].RootPath!.Trim()}) is the same as, or inside or around, the {all[j].Name} library's folder ({all[j].RootPath!.Trim()}). "
                        + "Give each library a folder of its own."));
                }
            }
        }

        return overlaps;
    }

    /// <summary>The overlaps that involve one library, in the layout where that library has the given folders.</summary>
    public static IReadOnlyList<LibraryFolderOverlap> FindOverlapsFor(IEnumerable<LibraryFolders> libraries, LibraryFolders candidate)
        => FindOverlaps(libraries.Where(item => !string.Equals(item.Id, candidate.Id, StringComparison.OrdinalIgnoreCase)).Append(candidate))
            .Where(overlap => string.Equals(overlap.LibraryId, candidate.Id, StringComparison.OrdinalIgnoreCase)
                || string.Equals(overlap.OtherLibraryId, candidate.Id, StringComparison.OrdinalIgnoreCase))
            .ToArray();

    /// <summary>
    /// The sentence that refuses a save, or null when it is fine. A save that leaves a library's three folders as they were is never
    /// refused here (the health check already shows that library's problem), so a person can still rename it.
    /// </summary>
    public static string? RefuseSave(IEnumerable<LibraryFolders> libraries, LibraryFolders candidate)
    {
        var existing = libraries.ToArray();
        var current = existing.FirstOrDefault(item => string.Equals(item.Id, candidate.Id, StringComparison.OrdinalIgnoreCase));
        if (current is not null
            && Same(current.RootPath, candidate.RootPath)
            && Same(current.DownloadsPath, candidate.DownloadsPath)
            && Same(current.ProcessorOutputPath, candidate.ProcessorOutputPath))
        {
            return null;
        }

        return FindOverlapsFor(existing, candidate).FirstOrDefault()?.Message;
    }

    /// <summary>
    /// Why clean-up must not act at all right now, or null. Every route and job that removes a folder or a file asks this first and stops
    /// while it answers.
    /// </summary>
    public static string? CleanupBlockedReason(IEnumerable<LibraryItem> libraries)
        => FindOverlaps(libraries.Select(LibraryFolders.From)).FirstOrDefault() is { } first
            ? first.Message + " Deluno will not clean anything up until that is fixed in Settings, Libraries."
            : null;

    /// <summary>True when the path is a library's root, inside one, or holds one: somewhere clean-up never removes.</summary>
    public static bool ReachesALibrary(IEnumerable<LibraryItem> libraries, string? path)
    {
        if (Full(path) is not { } full)
        {
            return false;
        }

        foreach (var library in libraries)
        {
            if (Full(library.RootPath) is { } root && (Holds(full, root) || Holds(root, full)))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<(string Role, string Folder)> WorkFolders(LibraryFolders library)
    {
        if (!string.IsNullOrWhiteSpace(library.DownloadsPath))
        {
            yield return (DownloadsRole, library.DownloadsPath.Trim());
        }

        if (!string.IsNullOrWhiteSpace(library.ProcessorOutputPath))
        {
            yield return (ProcessorOutputRole, library.ProcessorOutputPath.Trim());
        }
    }

    private static bool Same(string? a, string? b)
        => string.Equals(Full(a), Full(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>True when <paramref name="folder"/> is <paramref name="other"/> or holds it.</summary>
    private static bool Holds(string folder, string other)
        => SafePath.IsInside(folder, other, allowEqual: true);

    private static string? Full(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.Trim();
        }
    }
}
