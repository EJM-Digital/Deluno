using System.Text.Json;
using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;

namespace Deluno.Worker.Services;

/// <summary>What a look at a library's processor output folder found.</summary>
public enum ProcessorOutputFolderState
{
    /// <summary>The library does not hand to a processor, or has no output folder set: nothing to watch.</summary>
    NotWatched,
    Fine,
    Missing,
    Unreadable
}

/// <summary>
/// The processor's output folder for a library going missing or unreadable (#1514). Finished files wait in that folder,
/// so when it cannot be read the import scan has nothing to look at and, before this, said nothing. The watch writes one
/// Activity line when the folder goes missing (naming the library and the folder), none on later scans while it stays
/// that way, and one line when it is back. The Health row reads the same <see cref="Probe"/>, so the two always agree.
/// </summary>
public static class ProcessorOutputFolderWatch
{
    public const string MissingCategory = "processing.output.folder-missing";
    public const string BackCategory = "processing.output.folder-back";

    public static ProcessorOutputFolderState Probe(LibraryItem library)
    {
        if (!ImportWorkflows.HandsToProcessor(library.ImportWorkflow) || string.IsNullOrWhiteSpace(library.ProcessorOutputPath))
        {
            return ProcessorOutputFolderState.NotWatched;
        }

        try
        {
            if (!Directory.Exists(library.ProcessorOutputPath))
            {
                return ProcessorOutputFolderState.Missing;
            }

            _ = Directory.EnumerateFileSystemEntries(library.ProcessorOutputPath).FirstOrDefault();
            return ProcessorOutputFolderState.Fine;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return ProcessorOutputFolderState.Unreadable;
        }
    }

    /// <summary>The plain sentence for a folder that cannot be used; the same words in Activity and on Health.</summary>
    public static string Describe(LibraryItem library, ProcessorOutputFolderState state)
        => state == ProcessorOutputFolderState.Unreadable
            ? $"Deluno cannot read the processor output folder for {library.Name} ({library.ProcessorOutputPath}), so finished files waiting there are not being imported. Check the folder's permissions."
            : $"The processor output folder for {library.Name} ({library.ProcessorOutputPath}) is missing, so finished files waiting there are not being imported. Check that the drive or share is connected, or point the library at the right folder.";

    /// <summary>
    /// Looks at every library once and writes a line only when something changed since the last line about that library's
    /// folder. Returns the libraries whose folder cannot be used, so the caller skips them.
    /// </summary>
    public static async Task<IReadOnlySet<string>> RecordChangesAsync(
        IReadOnlyList<LibraryItem> libraries,
        IActivityFeedRepository activityFeedRepository,
        CancellationToken cancellationToken)
    {
        var unusable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in libraries)
        {
            var state = Probe(library);
            if (state == ProcessorOutputFolderState.NotWatched)
            {
                continue;
            }

            var problem = state is ProcessorOutputFolderState.Missing or ProcessorOutputFolderState.Unreadable;
            if (problem)
            {
                unusable.Add(library.Id);
            }

            var lines = await activityFeedRepository.ListActivityAsync(30, "library", library.Id, cancellationToken);
            var last = lines.FirstOrDefault(line => line.Category is MissingCategory or BackCategory);
            // Said again only when the folder or the way it fails changes; compared on the details, not the words,
            // because the feed may reword a stored message when it reads it back.
            var details = JsonSerializer.Serialize(new { LibraryId = library.Id, LibraryName = library.Name, library.ProcessorOutputPath, State = state.ToString() });

            if (problem && !(last?.Category == MissingCategory && last.DetailsJson == details))
            {
                await activityFeedRepository.RecordActivityAsync(
                    MissingCategory,
                    Describe(library, state),
                    details,
                    null,
                    "library",
                    library.Id,
                    cancellationToken);
            }
            else if (!problem && last?.Category == MissingCategory)
            {
                await activityFeedRepository.RecordActivityAsync(
                    BackCategory,
                    $"The processor output folder for {library.Name} ({library.ProcessorOutputPath}) is back. Finished files there are being imported again.",
                    JsonSerializer.Serialize(new { LibraryId = library.Id, LibraryName = library.Name, library.ProcessorOutputPath }),
                    null,
                    "library",
                    library.Id,
                    cancellationToken);
            }
        }

        return unusable;
    }
}
