namespace Deluno.Platform.Contracts;

public sealed record MigrationImportRequest(
    string? SourceKind,
    string? SourceName,
    string? PayloadJson,
    IReadOnlyList<string>? SelectedOperationIds = null,
    /// <summary>
    /// Stores otherwise-unmapped matcher rows as Advanced legacy input. The
    /// rows remain outside typed decision-making; this opt-in only preserves
    /// them for later owner review and export.
    /// </summary>
    bool AllowAdvancedLegacyRules = false);
