using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace Deluno.Downloader.Extraction;

/// <summary>
/// Zip / 7z / tar / tar.gz / tar.bz2 extraction via SharpCompress (managed,
/// BSD-licensed). RAR is handled separately by
/// <see cref="UnRarBinaryExtractor"/> because the official UnRAR binary
/// has the most complete RAR5 + multi-volume + encryption support, and
/// SharpCompress's RAR5 reader has known gaps.
/// </summary>
public sealed class SharpCompressArchiveExtractor : IArchiveExtractor
{
    public IReadOnlyCollection<ArchiveFormat> Supports { get; } =
    [
        ArchiveFormat.Zip,
        ArchiveFormat.SevenZip,
        ArchiveFormat.Tar,
        ArchiveFormat.TarGz,
        ArchiveFormat.TarBz2,
    ];

    public async Task<ArchiveExtractionResult> ExtractAsync(
        string archivePath,
        string outputDir,
        string? password,
        IProgress<ArchiveExtractionProgress>? progress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(outputDir);
        var extractedFiles = new List<string>();

        try
        {
            var readerOptions = new ReaderOptions
            {
                Password = password,
                LeaveStreamOpen = false,
            };

            // For random-access formats (zip / 7z), IArchive is more
            // efficient because it can extract specific entries without
            // re-scanning. For streaming formats (tar / tar.gz), IReader
            // is the right shape. We try IArchive first; SharpCompress
            // throws ArchiveException for streaming-only formats and we
            // fall back.
            try
            {
                return await ExtractWithArchive(archivePath, outputDir, readerOptions, progress, ct, extractedFiles);
            }
            catch (Exception ex) when (
                ex is ArchiveException ||
                ex is InvalidOperationException ||
                ex is NotSupportedException ||
                ex is NotImplementedException)
            {
                return await ExtractWithReader(archivePath, outputDir, readerOptions, progress, ct, extractedFiles);
            }
        }
        catch (Exception ex)
        {
            return new ArchiveExtractionResult(
                Succeeded: false,
                ExtractedFiles: extractedFiles,
                FailureReason: ex.Message);
        }
    }

    private static async Task<ArchiveExtractionResult> ExtractWithArchive(
        string archivePath, string outputDir, ReaderOptions options,
        IProgress<ArchiveExtractionProgress>? progress, CancellationToken ct,
        List<string> extractedFiles)
    {
        await using var stream = File.OpenRead(archivePath);
        using var archive = ArchiveFactory.Open(stream, options);

        var total = archive.TotalUncompressSize;
        long extracted = 0;

        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.IsDirectory) continue;

            var destPath = ResolveDestinationPath(outputDir, entry.Key ?? string.Empty);
            if (destPath is null) continue; // path traversal attempt — skip

            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            await using (var output = File.Create(destPath))
            {
                entry.WriteTo(output);
            }
            extractedFiles.Add(destPath);
            extracted += entry.Size;
            progress?.Report(new ArchiveExtractionProgress(entry.Key ?? "(unknown)", extracted, total));
        }

        return new ArchiveExtractionResult(true, extractedFiles, null);
    }

    private static async Task<ArchiveExtractionResult> ExtractWithReader(
        string archivePath, string outputDir, ReaderOptions options,
        IProgress<ArchiveExtractionProgress>? progress, CancellationToken ct,
        List<string> extractedFiles)
    {
        await using var stream = File.OpenRead(archivePath);
        using var reader = ReaderFactory.Open(stream, options);
        long extracted = 0;

        while (reader.MoveToNextEntry())
        {
            ct.ThrowIfCancellationRequested();
            if (reader.Entry.IsDirectory) continue;

            var destPath = ResolveDestinationPath(outputDir, reader.Entry.Key ?? string.Empty);
            if (destPath is null) continue;

            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            await using (var output = File.Create(destPath))
            {
                reader.WriteEntryTo(output);
            }
            extractedFiles.Add(destPath);
            extracted += reader.Entry.Size;
            progress?.Report(new ArchiveExtractionProgress(reader.Entry.Key ?? "(unknown)", extracted, 0));
        }

        return new ArchiveExtractionResult(true, extractedFiles, null);
    }

    /// <summary>
    /// Defends against path-traversal entries (<c>../../etc/passwd</c>).
    /// Returns null if the resolved path escapes <paramref name="outputDir"/>.
    /// </summary>
    private static string? ResolveDestinationPath(string outputDir, string entryKey)
    {
        if (string.IsNullOrWhiteSpace(entryKey)) return null;
        var fullOutput = Path.GetFullPath(outputDir);
        var candidate = Path.GetFullPath(Path.Combine(fullOutput, entryKey));
        if (!candidate.StartsWith(fullOutput, StringComparison.OrdinalIgnoreCase))
            return null;
        return candidate;
    }
}
