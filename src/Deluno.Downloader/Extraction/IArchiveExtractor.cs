namespace Deluno.Downloader.Extraction;

/// <summary>
/// Per-format archive extractor. The pipeline picks the right one based
/// on detected format.
/// </summary>
public interface IArchiveExtractor
{
    /// <summary>Formats this extractor handles.</summary>
    IReadOnlyCollection<ArchiveFormat> Supports { get; }

    Task<ArchiveExtractionResult> ExtractAsync(
        string archivePath,
        string outputDir,
        string? password,
        IProgress<ArchiveExtractionProgress>? progress,
        CancellationToken ct);
}

public sealed record ArchiveExtractionResult(
    bool Succeeded,
    IReadOnlyList<string> ExtractedFiles,
    string? FailureReason);

public sealed record ArchiveExtractionProgress(
    string CurrentFile,
    long BytesExtracted,
    long TotalBytes)
{
    public double Percent => TotalBytes == 0 ? 0 : (double)BytesExtracted / TotalBytes * 100;
}
