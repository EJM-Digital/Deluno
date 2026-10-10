using System.Data.Common;
using System.Globalization;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;

namespace Deluno.Platform.Data;

/// <summary>SQLite-backed home for every health check's latest result (#909). See <see cref="IHealthCheckRepository"/>.</summary>
public sealed class SqliteHealthCheckRepository(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider)
    : IHealthCheckRepository
{
    public async Task<IReadOnlyList<HealthCheckTransitionResult>> UpsertResultsAsync(
        IReadOnlyList<HealthCheckOutcome> outcomes,
        CancellationToken cancellationToken)
    {
        if (outcomes.Count == 0)
        {
            return [];
        }

        var now = timeProvider.GetUtcNow();
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var results = new List<HealthCheckTransitionResult>(outcomes.Count);

        foreach (var outcome in outcomes)
        {
            var existing = await ReadOneAsync(connection, transaction, outcome.Id, cancellationToken);
            var wasProblem = existing is { Status: HealthCheckStatuses.Problem };
            var isProblem = outcome.Status == HealthCheckStatuses.Problem;

            var problemSinceUtc = isProblem
                ? existing?.ProblemSinceUtc ?? now
                : (DateTimeOffset?)null;

            // Clearing a problem clears its snooze too -- a snooze silences a
            // known problem for a while, not the next, unrelated one that
            // happens to reuse the same id later.
            var snoozedUntilUtc = isProblem ? existing?.SnoozedUntilUtc : null;

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO health_check_results
                        (id, category, name, status, message, cause, fix, details_json, last_run_utc, problem_since_utc, snoozed_until_utc, created_utc, updated_utc)
                    VALUES
                        (@id, @category, @name, @status, @message, @cause, @fix, @details, @lastRun, @problemSince, @snoozedUntil, @created, @updated)
                    ON CONFLICT(id) DO UPDATE SET
                        category = excluded.category,
                        name = excluded.name,
                        status = excluded.status,
                        message = excluded.message,
                        cause = excluded.cause,
                        fix = excluded.fix,
                        details_json = excluded.details_json,
                        last_run_utc = excluded.last_run_utc,
                        problem_since_utc = excluded.problem_since_utc,
                        snoozed_until_utc = excluded.snoozed_until_utc,
                        updated_utc = excluded.updated_utc;
                    """;
                AddParameter(command, "@id", outcome.Id);
                AddParameter(command, "@category", outcome.Category);
                AddParameter(command, "@name", outcome.Name);
                AddParameter(command, "@status", outcome.Status);
                AddParameter(command, "@message", outcome.Message);
                AddParameter(command, "@cause", outcome.Cause);
                AddParameter(command, "@fix", outcome.Fix);
                AddParameter(command, "@details", outcome.DetailsJson);
                AddParameter(command, "@lastRun", now.ToString("O"));
                AddParameter(command, "@problemSince", problemSinceUtc?.ToString("O"));
                AddParameter(command, "@snoozedUntil", snoozedUntilUtc?.ToString("O"));
                AddParameter(command, "@created", (existing?.LastRunUtc ?? now).ToString("O"));
                AddParameter(command, "@updated", now.ToString("O"));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            var updatedItem = new HealthCheckResultItem(
                outcome.Id, outcome.Category, outcome.Name, outcome.Status, outcome.Message,
                outcome.Cause, outcome.Fix, outcome.DetailsJson, now, problemSinceUtc, snoozedUntilUtc);

            var transition = !wasProblem && isProblem
                ? HealthCheckTransition.Started
                : wasProblem && !isProblem
                    ? HealthCheckTransition.Cleared
                    : HealthCheckTransition.Unchanged;

            var screenChanged = existing is null
                || !string.Equals(existing.Status, outcome.Status, StringComparison.Ordinal)
                || !string.Equals(existing.Message, outcome.Message, StringComparison.Ordinal);
            results.Add(new HealthCheckTransitionResult(updatedItem, transition, screenChanged));
        }

        await transaction.CommitAsync(cancellationToken);
        return results;
    }

    public async Task<IReadOnlyList<HealthCheckResultItem>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenReadOnlyConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, category, name, status, message, cause, fix, details_json, last_run_utc, problem_since_utc, snoozed_until_utc
            FROM health_check_results
            ORDER BY
                CASE status WHEN 'problem' THEN 0 WHEN 'unknown' THEN 1 WHEN 'suggestion' THEN 2 ELSE 3 END,
                category, name;
            """;

        var items = new List<HealthCheckResultItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(Read(reader));
        }

        return items;
    }

    public async Task<HealthCheckResultItem?> GetAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenReadOnlyConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        return await ReadOneAsync(connection, null, id, cancellationToken);
    }

    public async Task<HealthCheckSummary> GetSummaryAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var all = await ListAsync(cancellationToken);
        var problems = all.Where(item => item.Status == HealthCheckStatuses.Problem).ToArray();
        return new HealthCheckSummary(
            ProblemCount: problems.Length,
            UnsnoozedProblemCount: problems.Count(item => !item.IsSnoozed(now)),
            UnknownCount: all.Count(item => item.Status == HealthCheckStatuses.Unknown),
            SuggestionCount: all.Count(item => item.Status == HealthCheckStatuses.Suggestion),
            LastRunUtc: all.Count == 0 ? null : all.Max(item => item.LastRunUtc));
    }

    public async Task<HealthCheckResultItem?> SnoozeAsync(string id, DateTimeOffset? untilUtc, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        var existing = await ReadOneAsync(connection, null, id, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE health_check_results SET snoozed_until_utc = @snoozedUntil, updated_utc = @updated WHERE id = @id;";
        AddParameter(command, "@snoozedUntil", untilUtc?.ToString("O"));
        AddParameter(command, "@updated", timeProvider.GetUtcNow().ToString("O"));
        AddParameter(command, "@id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);

        return existing with { SnoozedUntilUtc = untilUtc };
    }

    public async Task RemoveAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM health_check_results WHERE id = @id;";
        AddParameter(command, "@id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RequestImmediateRunAsync(string category, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO health_check_run_requests (category, requested_utc)
            VALUES (@category, @requestedUtc)
            ON CONFLICT(category) DO UPDATE SET requested_utc = excluded.requested_utc;
            """;
        AddParameter(command, "@category", category);
        AddParameter(command, "@requestedUtc", timeProvider.GetUtcNow().ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> TakePendingImmediateRunsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var categories = new List<string>();
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT category FROM health_check_run_requests;";
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                categories.Add(reader.GetString(0));
            }
        }

        if (categories.Count > 0)
        {
            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM health_check_run_requests;";
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return categories;
    }

    private static async Task<HealthCheckResultItem?> ReadOneAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string id,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT id, category, name, status, message, cause, fix, details_json, last_run_utc, problem_since_utc, snoozed_until_utc
            FROM health_check_results
            WHERE id = @id;
            """;
        AddParameter(command, "@id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    private static HealthCheckResultItem Read(DbDataReader reader) => new(
        Id: reader.GetString(0),
        Category: reader.GetString(1),
        Name: reader.GetString(2),
        Status: reader.GetString(3),
        Message: reader.GetString(4),
        Cause: reader.IsDBNull(5) ? null : reader.GetString(5),
        Fix: reader.IsDBNull(6) ? null : reader.GetString(6),
        DetailsJson: reader.IsDBNull(7) ? null : reader.GetString(7),
        LastRunUtc: DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        ProblemSinceUtc: reader.IsDBNull(9) ? null : DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        SnoozedUntilUtc: reader.IsDBNull(10) ? null : DateTimeOffset.Parse(reader.GetString(10), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
