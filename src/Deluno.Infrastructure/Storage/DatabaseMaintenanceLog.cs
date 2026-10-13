using System.Text.Json;
using Deluno.Contracts;

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

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    /// <summary>What was last done; "never" for a note that is missing, busy or damaged. A change never starts from this: see <see cref="Update"/>.</summary>
    public static DatabaseMaintenance Read(string dataRoot, IStateFileSystem? io = null)
        => StateFile.Read<DatabaseMaintenance>(Path.Combine(dataRoot, FileName), Json, io).Value ?? Empty;

    public static string? RecordIntegrity(string dataRoot, DateTimeOffset checkedUtc, bool ok, string? note, IStateFileSystem? io = null)
        => Update(dataRoot, current => current with { IntegrityCheckedUtc = checkedUtc, IntegrityOk = ok, IntegrityNote = note }, io);

    public static string? RecordCompaction(string dataRoot, DateTimeOffset compactedUtc, IStateFileSystem? io = null)
        => Update(dataRoot, current => current with { CompactedUtc = compactedUtc }, io);

    private static DatabaseMaintenance Empty => new(null, null, null, null);

    /// <summary>
    /// Reads the note again under its lock and changes one part of it, so a busy read never turns the other parts into "never"
    /// (#1260). A note that is busy is left as it is. One that is damaged has a copy kept and starts again: all it holds is when a
    /// check last ran. Null when it landed, else why not, in everyday words; a note that was not kept costs only a line of text on a screen.
    /// </summary>
    private static string? Update(string dataRoot, Func<DatabaseMaintenance, DatabaseMaintenance> change, IStateFileSystem? io)
        => StateFile.Update<DatabaseMaintenance>(
            Path.Combine(dataRoot, FileName),
            current => change(current ?? Empty),
            "The note of when the databases were last checked",
            Json,
            io,
            UnreadableBehavior.StartFresh).Problem;
}
