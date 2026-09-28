using Deluno.Contracts;
using Deluno.Platform.Contracts;

namespace Deluno.Platform.Processing;

/// <summary>
/// Applies a processor connection's path mappings (#962) at the two points a
/// path crosses the boundary: outbound on submission, and inbound on a
/// completion callback. Built on the same longest-match algorithm download
/// client remote path mappings use (#809,
/// <see cref="Deluno.Contracts.PathMappingTranslator"/>), not a second one.
/// With no mappings, both directions pass the path through unchanged.
/// </summary>
public static class ProcessorPathMappingTranslator
{
    /// <summary>A path as Deluno sees it (a hand-off's sourcePath), translated to what the processor should see.</summary>
    public static string? ToProcessor(string? delunoPath, IReadOnlyList<ProcessorPathMappingItem> mappings)
        => PathMappingTranslator.Translate(
            delunoPath,
            mappings,
            item => item.IsEnabled,
            item => item.DelunoPath,
            item => item.ProcessorPath,
            item => item.Priority);

    /// <summary>A path as the processor reported it (outputPath, or one of outputFiles), translated back to Deluno's own view.</summary>
    public static string? ToDeluno(string? processorPath, IReadOnlyList<ProcessorPathMappingItem> mappings)
        => PathMappingTranslator.Translate(
            processorPath,
            mappings,
            item => item.IsEnabled,
            item => item.ProcessorPath,
            item => item.DelunoPath,
            item => item.Priority);
}
