namespace Deluno.Platform.Contracts;

/// <summary>
/// Reconciled, secret-free counts for one migration preview. The detailed
/// operation rows remain the source of per-record provenance; this inventory
/// makes it possible to verify that the import did not silently drop a legacy
/// row before anything is applied.
/// </summary>
public sealed record MigrationReportInventory(
    int InputRowCount,
    int AccountedRowCount,
    int UnaccountedRowCount,
    IReadOnlyList<MigrationInventoryEntry> Entries)
{
    public static MigrationReportInventory Empty { get; } = new(0, 0, 0, []);
}

public sealed record MigrationInventoryEntry(
    string SourceKind,
    string MediaType,
    string Category,
    int InputRowCount,
    int AccountedRowCount,
    IReadOnlyDictionary<string, int> ActionCounts,
    IReadOnlyDictionary<string, int> ClassificationCounts,
    IReadOnlyList<string> Warnings)
{
    public int UnaccountedRowCount => Math.Max(0, InputRowCount - AccountedRowCount);

    public bool Complete => UnaccountedRowCount == 0;
}
