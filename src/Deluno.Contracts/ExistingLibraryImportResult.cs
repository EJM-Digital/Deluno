namespace Deluno.Contracts;

public sealed record ExistingLibraryImportResult(
    string LibraryId,
    string LibraryName,
    string MediaType,
    string RootPath,
    int DiscoveredCount,
    int ImportedCount,
    int SkippedCount,
    IReadOnlyList<string> SampleTitles);
