namespace Deluno.Platform.Contracts;

/// <summary>
/// Translates a path at a processor connection's boundary (#962): a processor
/// running in a container or on another machine sees the shared download and
/// clean-output folders under a different path than Deluno does, the same
/// problem download clients already solve (#809,
/// <see cref="Deluno.Connections.Contracts.DownloadClientPathMappingItem"/>).
///
/// <para><see cref="DelunoPath"/> is the path as Deluno sees it; <see
/// cref="ProcessorPath"/> is the same folder as the processor sees it. A
/// hand-off's <c>sourcePath</c> is translated Deluno -> processor on
/// submission; a completion's <c>outputPath</c> and <c>outputFiles</c> are
/// translated processor -> Deluno in reverse, before import.</para>
/// </summary>
public sealed record ProcessorPathMappingItem(
    string Id,
    string ProcessorConnectionId,
    string DelunoPath,
    string ProcessorPath,
    bool IsEnabled,
    int Priority,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

public sealed record CreateProcessorPathMappingRequest(
    string? DelunoPath,
    string? ProcessorPath,
    bool IsEnabled,
    int? Priority);
