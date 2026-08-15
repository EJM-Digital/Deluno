using Deluno.Platform.Contracts;

namespace Deluno.Platform.Migration;

/// <summary>
/// Extension point for importing catalog records without making the Platform
/// module depend on Movies or Series. Each media module owns its own durable
/// catalog and decides how a migration item is safely represented there.
/// </summary>
public interface IMigrationCatalogImporter
{
    string MediaType { get; }

    Task<MigrationCatalogImportResult> ImportAsync(
        MigrationCatalogImportRequest request,
        CancellationToken cancellationToken);
}

public sealed record MigrationCatalogImportRequest(
    string SourceKind,
    string SourceName,
    IReadOnlyList<MigrationCatalogTitle> Titles,
    IReadOnlyList<MigrationCatalogLibrary> Libraries);

public sealed record MigrationCatalogTitle(
    string MediaType,
    string Title,
    int? Year,
    string? ImdbId,
    string? MetadataProvider,
    string? MetadataProviderId,
    bool Monitored,
    bool SourceReportsFile,
    string? RootPath);

public sealed record MigrationCatalogLibrary(
    string Id,
    string MediaType,
    string RootPath,
    string Name);

public sealed record MigrationCatalogImportResult(
    IReadOnlyList<MigrationAppliedItem> Applied,
    IReadOnlyList<string> Warnings);
