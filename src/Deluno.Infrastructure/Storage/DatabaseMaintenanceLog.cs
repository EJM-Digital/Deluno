using System.Text.Json;

namespace Deluno.Infrastructure.Storage;

/// <summary>What Deluno last did to keep its databases healthy (#1098).</summary>
public sealed record DatabaseMaintenance(
    DateTimeOffset? IntegrityCheckedUtc,
    bool? IntegrityOk,
    string? IntegrityNote,
    DateTimeOffset? CompactedUtc);

/// <summary>
/// The time of the last integrity check (and what it found) and of the last compaction, kept in one
/// small file beside the databases so the System screen can say "integrity checked 4h ago ·
/// compacted 3d ago" without a schema change. Written by the daily integrity check and by the
/// migrator after it compacts a database; a missing or unreadable file means "never".
/// </summary>
public static class DatabaseMaintenanceLog
{
    public const string FileName = "database-maintenance.json";

    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public static DatabaseMaintenance Read(string dataRoot)
    {
        lock (Gate)
        {
            try
            {
                var path = Path.Combine(dataRoot, FileName);
                return File.Exists(path)
                    ? JsonSerializer.Deserialize<DatabaseMaintenance>(File.ReadAllText(path), Json) ?? Empty
                    : Empty;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                return Empty;
            }
        }
    }

    public static void RecordIntegrity(string dataRoot, DateTimeOffset checkedUtc, bool ok, string? note)
        => Update(dataRoot, current => current with { IntegrityCheckedUtc = checkedUtc, IntegrityOk = ok, IntegrityNote = note });

    public static void RecordCompaction(string dataRoot, DateTimeOffset compactedUtc)
        => Update(dataRoot, current => current with { CompactedUtc = compactedUtc });

    private static DatabaseMaintenance Empty => new(null, null, null, null);

    private static void Update(string dataRoot, Func<DatabaseMaintenance, DatabaseMaintenance> change)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(dataRoot);
                var next = change(Read(dataRoot));
                File.WriteAllText(Path.Combine(dataRoot, FileName), JsonSerializer.Serialize(next, Json));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A note that could not be written costs only a line of text on a screen.
            }
        }
    }
}
