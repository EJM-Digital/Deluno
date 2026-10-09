using System.Text.Json;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Platform.Contracts;
using Deluno.Platform.Processing;

namespace Deluno.Worker.Services;

/// <summary>
/// Builds the hand-off as it goes over the wire (#1199): the source path translated to the processor's, and, when the
/// processor advertises a minimum file size for the library, the files at or above it named in <c>outputFiles</c>.
///
/// <para>The processor refuses a file strictly smaller than its minimum, so a file of exactly the minimum is named.
/// The smaller ones are not touched: they stay in the download folder, where seeding keeps them safe, and go only
/// when Deluno removes the whole download. Nothing here deletes, moves or renames a file. With no minimum advertised,
/// a source that is a single file, or no file at or above the minimum, the hand-off is the whole folder as before.</para>
/// </summary>
public static class HandoffFileSelection
{
    public static async Task<ProcessorHandoffItem> ForProcessorAsync(
        ProcessorHandoffItem handoff,
        LibraryItem library,
        ProcessorConnectionItem connection,
        IReadOnlyList<ProcessorPathMappingItem> mappings,
        IProcessorConnectionService processorConnections,
        IActivityFeedRepository activityFeed,
        CancellationToken cancellationToken)
    {
        var outbound = mappings.Count > 0
            ? handoff with { SourcePath = ProcessorPathMappingTranslator.ToProcessor(handoff.SourcePath, mappings) ?? handoff.SourcePath }
            : handoff;

        long? minimum = null;
        try
        {
            var folders = await processorConnections.GetLibraryFoldersAsync(connection, cancellationToken);
            minimum = ProcessorLibraryFolder.ForLibrary(folders, handoff.LibraryId)?.MinimumFileSizeBytes;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Not knowing the minimum is today's behaviour: the whole folder.
        }

        if (minimum is not { } floor || floor <= 0 || !Directory.Exists(handoff.SourcePath))
        {
            return outbound;
        }

        List<(string Path, long Length)> files;
        try
        {
            files = new DirectoryInfo(handoff.SourcePath)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Select(file => (file.FullName, file.Length))
                .ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return outbound;
        }

        var named = files.Where(file => file.Length >= floor).Select(file => file.Path).Order(StringComparer.Ordinal).ToList();
        var left = files.Where(file => file.Length < floor).Select(file => file.Path).Order(StringComparer.Ordinal).ToList();
        if (left.Count == 0 || named.Count == 0)
        {
            return outbound;
        }

        var relative = left.Select(path => Path.GetRelativePath(handoff.SourcePath, path).Replace(Path.DirectorySeparatorChar, '/')).ToList();
        var countText = relative.Count == 1 ? "1 small file was" : $"{relative.Count} small files were";
        var shown = string.Join(", ", relative.Take(5)) + (relative.Count > 5 ? ", and more" : string.Empty);
        await activityFeed.RecordActivityOnChangeAsync(
            "processing.left-in-download",
            $"{handoff.ReleaseName}: {countText} left in the download ({shown}), because {connection.Name} skips files under {FormatSize(floor)}. They stay where they are and go with the download when Deluno removes it after seeding.",
            JsonSerializer.Serialize(new { HandoffId = handoff.Id, handoff.LibraryId, handoff.ReleaseName, MinimumFileSizeBytes = floor, LeftInDownload = relative }),
            null,
            "processor-handoff",
            handoff.Id,
            cancellationToken);

        return outbound with
        {
            OutputFiles = named
                .Select(path => mappings.Count > 0 ? ProcessorPathMappingTranslator.ToProcessor(path, mappings) ?? path : path)
                .ToList()
        };
    }

    private static string FormatSize(long bytes)
        => bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024.0):0.#} MB" : bytes >= 1024 ? $"{bytes / 1024.0:0.#} KB" : $"{bytes} bytes";
}
