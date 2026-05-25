namespace Deluno.Downloader.Extraction;

/// <summary>
/// Top-level extraction entry point used by the orchestrator. Detects
/// the archive format then dispatches to the registered
/// <see cref="IArchiveExtractor"/> that handles it.
///
/// Registered extractors are passed in via DI; the order of registration
/// determines the dispatch priority for formats handled by multiple
/// extractors (currently nothing overlaps, but future managed-RAR vs
/// binary-RAR setups might).
/// </summary>
public sealed class ArchiveExtractionPipeline
{
    private readonly IReadOnlyDictionary<ArchiveFormat, IArchiveExtractor> _byFormat;

    public ArchiveExtractionPipeline(IEnumerable<IArchiveExtractor> extractors)
    {
        var map = new Dictionary<ArchiveFormat, IArchiveExtractor>();
        foreach (var ex in extractors)
            foreach (var fmt in ex.Supports)
                map.TryAdd(fmt, ex); // first wins; later registrations are fallbacks
        _byFormat = map;
    }

    public async Task<ArchiveExtractionResult> ExtractAsync(
        string archivePath,
        string outputDir,
        string? password,
        IProgress<ArchiveExtractionProgress>? progress,
        CancellationToken ct)
    {
        var format = ArchiveFormatDetector.DetectByExtension(archivePath);
        if (format == ArchiveFormat.Unknown)
            format = await ArchiveFormatDetector.DetectByMagicAsync(archivePath, ct);

        if (format == ArchiveFormat.Unknown)
            return new ArchiveExtractionResult(false, Array.Empty<string>(),
                $"Could not detect archive format for '{archivePath}'.");

        if (!_byFormat.TryGetValue(format, out var extractor))
            return new ArchiveExtractionResult(false, Array.Empty<string>(),
                $"No extractor registered for format {format}.");

        return await extractor.ExtractAsync(archivePath, outputDir, password, progress, ct);
    }
}
