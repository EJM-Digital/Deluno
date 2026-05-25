using System.Data;
using System.Globalization;
using Deluno.Downloader.Engine;
using Deluno.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace Deluno.Downloader.Persistence;

/// <summary>
/// SQLite-backed <see cref="IJobRepository"/>. Uses the shared
/// <c>downloader.db</c> connection factory from
/// <c>Deluno.Infrastructure.Storage</c>.
/// </summary>
public sealed class SqliteJobRepository(IDelunoDatabaseConnectionFactory connectionFactory)
    : IJobRepository
{
    private const string DbName = DelunoDatabaseNames.Downloader;

    public async Task<JobRecord?> GetAsync(string id, CancellationToken ct)
    {
        await using var conn = await connectionFactory.OpenConnectionAsync(DbName, ct);
        await using var cmd = (SqliteCommand)conn.CreateCommand();
        cmd.CommandText = "SELECT " + AllColumns + " FROM jobs WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapJob(reader) : null;
    }

    public async Task<IReadOnlyList<JobRecord>> ListByStateAsync(
        IReadOnlyList<JobLifecycleState> states, int limit, CancellationToken ct)
    {
        if (states.Count == 0) return Array.Empty<JobRecord>();

        await using var conn = await connectionFactory.OpenConnectionAsync(DbName, ct);
        await using var cmd = (SqliteCommand)conn.CreateCommand();
        var placeholders = string.Join(",", states.Select((_, i) => $"$s{i}"));
        cmd.CommandText =
            $"SELECT {AllColumns} FROM jobs WHERE state IN ({placeholders}) " +
            "ORDER BY priority DESC, created_at ASC LIMIT $limit;";
        for (var i = 0; i < states.Count; i++)
            cmd.Parameters.AddWithValue($"$s{i}", states[i].ToString());
        cmd.Parameters.AddWithValue("$limit", limit);

        return await ReadJobs(cmd, ct);
    }

    public async Task<IReadOnlyList<JobRecord>> ListPriorityOrderedAsync(
        JobLifecycleState state, int limit, CancellationToken ct)
    {
        await using var conn = await connectionFactory.OpenConnectionAsync(DbName, ct);
        await using var cmd = (SqliteCommand)conn.CreateCommand();
        cmd.CommandText =
            $"SELECT {AllColumns} FROM jobs WHERE state = $state " +
            "ORDER BY priority DESC, created_at ASC LIMIT $limit;";
        cmd.Parameters.AddWithValue("$state", state.ToString());
        cmd.Parameters.AddWithValue("$limit", limit);
        return await ReadJobs(cmd, ct);
    }

    public async Task UpsertAsync(JobRecord job, CancellationToken ct)
    {
        await using var conn = await connectionFactory.OpenConnectionAsync(DbName, ct);
        await using var cmd = (SqliteCommand)conn.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO jobs (
                id, protocol, display_name, source_path, source_kind, category, priority,
                state, state_reason, paused, password_protected, download_dir, output_dir,
                total_bytes, downloaded_bytes, uploaded_bytes, dispatch_id, library_id,
                created_at, updated_at, completed_at
            ) VALUES (
                $id, $protocol, $display_name, $source_path, $source_kind, $category, $priority,
                $state, $state_reason, $paused, $password_protected, $download_dir, $output_dir,
                $total_bytes, $downloaded_bytes, $uploaded_bytes, $dispatch_id, $library_id,
                $created_at, $updated_at, $completed_at
            )
            ON CONFLICT(id) DO UPDATE SET
                display_name = excluded.display_name,
                category = excluded.category,
                priority = excluded.priority,
                state = excluded.state,
                state_reason = excluded.state_reason,
                paused = excluded.paused,
                password_protected = excluded.password_protected,
                download_dir = excluded.download_dir,
                output_dir = excluded.output_dir,
                total_bytes = excluded.total_bytes,
                downloaded_bytes = excluded.downloaded_bytes,
                uploaded_bytes = excluded.uploaded_bytes,
                dispatch_id = excluded.dispatch_id,
                library_id = excluded.library_id,
                updated_at = excluded.updated_at,
                completed_at = excluded.completed_at;
            """;
        BindJob(cmd, job);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task TransitionAsync(
        string jobId, JobLifecycleState to, string? reason,
        DateTimeOffset occurredAt, CancellationToken ct)
    {
        await using var conn = await connectionFactory.OpenConnectionAsync(DbName, ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            JobLifecycleState? fromState = null;
            DownloadProtocol protocol;

            // Read current state + protocol with the transaction so we
            // serialize against concurrent writers.
            await using (var read = (SqliteCommand)conn.CreateCommand())
            {
                read.Transaction = tx;
                read.CommandText = "SELECT state, protocol FROM jobs WHERE id = $id;";
                read.Parameters.AddWithValue("$id", jobId);
                await using var r = await read.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct))
                    throw new InvalidOperationException($"Job '{jobId}' not found.");
                fromState = Enum.Parse<JobLifecycleState>(r.GetString(0));
                protocol = DownloadProtocolExtensions.FromDbValue(r.GetString(1));
            }

            JobLifecycleTransitions.EnsureLegal(fromState.Value, to, protocol);

            await using (var upd = (SqliteCommand)conn.CreateCommand())
            {
                upd.Transaction = tx;
                upd.CommandText =
                    "UPDATE jobs SET state = $state, state_reason = $reason, updated_at = $updated " +
                    "WHERE id = $id;";
                upd.Parameters.AddWithValue("$state", to.ToString());
                upd.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
                upd.Parameters.AddWithValue("$updated", occurredAt.ToString("O", CultureInfo.InvariantCulture));
                upd.Parameters.AddWithValue("$id", jobId);
                await upd.ExecuteNonQueryAsync(ct);
            }

            await using (var trans = (SqliteCommand)conn.CreateCommand())
            {
                trans.Transaction = tx;
                trans.CommandText =
                    "INSERT INTO state_transitions (job_id, from_state, to_state, reason, occurred_at) " +
                    "VALUES ($job_id, $from, $to, $reason, $occurred);";
                trans.Parameters.AddWithValue("$job_id", jobId);
                trans.Parameters.AddWithValue("$from", (object?)fromState?.ToString() ?? DBNull.Value);
                trans.Parameters.AddWithValue("$to", to.ToString());
                trans.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
                trans.Parameters.AddWithValue("$occurred", occurredAt.ToString("O", CultureInfo.InvariantCulture));
                await trans.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<IReadOnlyList<StateTransitionRecord>> GetTransitionsAsync(
        string jobId, CancellationToken ct)
    {
        await using var conn = await connectionFactory.OpenConnectionAsync(DbName, ct);
        await using var cmd = (SqliteCommand)conn.CreateCommand();
        cmd.CommandText =
            "SELECT id, job_id, from_state, to_state, reason, occurred_at " +
            "FROM state_transitions WHERE job_id = $id ORDER BY occurred_at ASC, id ASC;";
        cmd.Parameters.AddWithValue("$id", jobId);

        var results = new List<StateTransitionRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new StateTransitionRecord(
                Id: reader.GetInt64(0),
                JobId: reader.GetString(1),
                FromState: reader.IsDBNull(2) ? null : Enum.Parse<JobLifecycleState>(reader.GetString(2)),
                ToState: Enum.Parse<JobLifecycleState>(reader.GetString(3)),
                Reason: reader.IsDBNull(4) ? null : reader.GetString(4),
                OccurredAt: DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture)));
        }
        return results;
    }

    private const string AllColumns =
        "id, protocol, display_name, source_path, source_kind, category, priority, " +
        "state, state_reason, paused, password_protected, download_dir, output_dir, " +
        "total_bytes, downloaded_bytes, uploaded_bytes, dispatch_id, library_id, " +
        "created_at, updated_at, completed_at";

    private static async Task<IReadOnlyList<JobRecord>> ReadJobs(SqliteCommand cmd, CancellationToken ct)
    {
        var results = new List<JobRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(MapJob(reader));
        return results;
    }

    private static JobRecord MapJob(IDataRecord r)
    {
        DateTimeOffset? completed = r.IsDBNull(20) ? null
            : DateTimeOffset.Parse(r.GetString(20), CultureInfo.InvariantCulture);
        return new JobRecord(
            Id: r.GetString(0),
            Protocol: DownloadProtocolExtensions.FromDbValue(r.GetString(1)),
            DisplayName: r.GetString(2),
            SourcePath: r.GetString(3),
            SourceKind: r.GetString(4),
            Category: r.IsDBNull(5) ? null : r.GetString(5),
            Priority: r.GetInt32(6),
            State: Enum.Parse<JobLifecycleState>(r.GetString(7)),
            StateReason: r.IsDBNull(8) ? null : r.GetString(8),
            Paused: r.GetInt64(9) != 0,
            PasswordProtected: r.IsDBNull(10) ? null : r.GetString(10),
            DownloadDir: r.GetString(11),
            OutputDir: r.IsDBNull(12) ? null : r.GetString(12),
            TotalBytes: r.GetInt64(13),
            DownloadedBytes: r.GetInt64(14),
            UploadedBytes: r.GetInt64(15),
            DispatchId: r.IsDBNull(16) ? null : r.GetString(16),
            LibraryId: r.IsDBNull(17) ? null : r.GetString(17),
            CreatedAt: DateTimeOffset.Parse(r.GetString(18), CultureInfo.InvariantCulture),
            UpdatedAt: DateTimeOffset.Parse(r.GetString(19), CultureInfo.InvariantCulture),
            CompletedAt: completed);
    }

    private static void BindJob(SqliteCommand cmd, JobRecord job)
    {
        cmd.Parameters.AddWithValue("$id", job.Id);
        cmd.Parameters.AddWithValue("$protocol", job.Protocol.ToDbValue());
        cmd.Parameters.AddWithValue("$display_name", job.DisplayName);
        cmd.Parameters.AddWithValue("$source_path", job.SourcePath);
        cmd.Parameters.AddWithValue("$source_kind", job.SourceKind);
        cmd.Parameters.AddWithValue("$category", (object?)job.Category ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$priority", job.Priority);
        cmd.Parameters.AddWithValue("$state", job.State.ToString());
        cmd.Parameters.AddWithValue("$state_reason", (object?)job.StateReason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$paused", job.Paused ? 1 : 0);
        cmd.Parameters.AddWithValue("$password_protected", (object?)job.PasswordProtected ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$download_dir", job.DownloadDir);
        cmd.Parameters.AddWithValue("$output_dir", (object?)job.OutputDir ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$total_bytes", job.TotalBytes);
        cmd.Parameters.AddWithValue("$downloaded_bytes", job.DownloadedBytes);
        cmd.Parameters.AddWithValue("$uploaded_bytes", job.UploadedBytes);
        cmd.Parameters.AddWithValue("$dispatch_id", (object?)job.DispatchId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$library_id", (object?)job.LibraryId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created_at", job.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$updated_at", job.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue(
            "$completed_at",
            job.CompletedAt.HasValue ? job.CompletedAt.Value.ToString("O", CultureInfo.InvariantCulture) : (object)DBNull.Value);
    }
}
