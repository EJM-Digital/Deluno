namespace Deluno.Persistence.Tests.Migrations.Upgrade;

/// <summary>
/// A migration that rewrites, moves, merges, renames or deletes data rather than only adding tables,
/// columns or indexes. <see cref="Allowed"/> names the tables ("table") or columns ("table.column") whose
/// existing rows it may change; <see cref="Tests"/> names the hand-written tests that prove what each row
/// means afterwards. Everything not listed here must leave every existing row exactly as it was.
/// </summary>
internal sealed record DataChange(string Database, int Version, string[] Allowed, params (Type TestClass, string Method)[] Tests);

internal static class DataChangingMigrations
{
    public static IReadOnlyList<DataChange> All { get; } =
    [
    ];

    public static DataChange? Find(string database, int version)
        => All.SingleOrDefault(change => change.Database == database && change.Version == version);
}
