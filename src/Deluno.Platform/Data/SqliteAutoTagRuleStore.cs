using System.Data.Common;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using static Deluno.Infrastructure.Storage.SqliteRecordHelpers;

namespace Deluno.Platform.Data;

/// <summary>
/// Auto-tagging rules (#1024) in the platform database. A rule's tags are one
/// comma-separated column and each condition is its own row, which is what lets
/// <see cref="SqliteTagReferenceStore"/> rename or refuse to delete a tag a rule
/// gives or looks for.
/// </summary>
public sealed class SqliteAutoTagRuleStore(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider) : IAutoTagRuleStore
{
    public async Task<IReadOnlyList<AutoTagRule>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        return await ReadAsync(connection, null, cancellationToken);
    }

    public async Task<AutoTagRule?> GetAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        return (await ReadAsync(connection, id, cancellationToken)).FirstOrDefault();
    }

    public async Task<AutoTagRule> CreateAsync(SaveAutoTagRuleRequest request, CancellationToken cancellationToken)
    {
        var id = Guid.CreateVersion7().ToString("N");
        var now = timeProvider.GetUtcNow().ToString("O");
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await Exec(connection, transaction,
            """
            INSERT INTO auto_tag_rules (id, name, tags, remove_automatically, applies_to, is_enabled, created_utc, updated_utc)
            VALUES (@id, @name, @tags, @remove, @appliesTo, @enabled, @now, @now);
            """,
            cancellationToken,
            ("@id", id),
            ("@name", request.Name!.Trim()),
            ("@tags", JoinTags(request.Tags)),
            ("@remove", request.RemoveAutomatically == true ? 1 : 0),
            ("@appliesTo", AutoTagScopes.Normalize(request.AppliesTo)),
            ("@enabled", request.IsEnabled == false ? 0 : 1),
            ("@now", now));
        await WriteSpecificationsAsync(connection, transaction, id, request.Specifications, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await ReadAsync(connection, id, cancellationToken)).First();
    }

    public async Task<AutoTagRule?> UpdateAsync(string id, SaveAutoTagRuleRequest request, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var changed = await Exec(connection, transaction,
            """
            UPDATE auto_tag_rules
            SET name = @name, tags = @tags, remove_automatically = @remove, applies_to = @appliesTo,
                is_enabled = @enabled, updated_utc = @now
            WHERE id = @id;
            """,
            cancellationToken,
            ("@id", id),
            ("@name", request.Name!.Trim()),
            ("@tags", JoinTags(request.Tags)),
            ("@remove", request.RemoveAutomatically == true ? 1 : 0),
            ("@appliesTo", AutoTagScopes.Normalize(request.AppliesTo)),
            ("@enabled", request.IsEnabled == false ? 0 : 1),
            ("@now", timeProvider.GetUtcNow().ToString("O")));
        if (changed == 0)
        {
            return null;
        }

        await Exec(connection, transaction, "DELETE FROM auto_tag_specifications WHERE rule_id = @id;", cancellationToken, ("@id", id));
        await WriteSpecificationsAsync(connection, transaction, id, request.Specifications, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await ReadAsync(connection, id, cancellationToken)).FirstOrDefault();
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await Exec(connection, transaction, "DELETE FROM auto_tag_specifications WHERE rule_id = @id;", cancellationToken, ("@id", id));
        var removed = await Exec(connection, transaction, "DELETE FROM auto_tag_rules WHERE id = @id;", cancellationToken, ("@id", id)) > 0;
        await transaction.CommitAsync(cancellationToken);
        return removed;
    }

    private static string JoinTags(IReadOnlyList<string>? tags) =>
        string.Join(",", (tags ?? [])
            .Select(tag => tag.Trim())
            .Where(tag => tag.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase));

    private static async Task WriteSpecificationsAsync(
        DbConnection connection,
        DbTransaction transaction,
        string ruleId,
        IReadOnlyList<AutoTagSpecification>? specifications,
        CancellationToken cancellationToken)
    {
        var order = 0;
        foreach (var spec in specifications ?? [])
        {
            await Exec(connection, transaction,
                "INSERT INTO auto_tag_specifications (id, rule_id, sort_order, spec_type, negate, value) VALUES (@id, @ruleId, @order, @type, @negate, @value);",
                cancellationToken,
                ("@id", Guid.CreateVersion7().ToString("N")),
                ("@ruleId", ruleId),
                ("@order", order++),
                ("@type", spec.Type),
                ("@negate", spec.Negate ? 1 : 0),
                ("@value", (spec.Value ?? string.Empty).Trim()));
        }
    }

    private static async Task<List<AutoTagRule>> ReadAsync(DbConnection connection, string? id, CancellationToken cancellationToken)
    {
        var specsByRule = new Dictionary<string, List<AutoTagSpecification>>(StringComparer.Ordinal);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, rule_id, spec_type, negate, value FROM auto_tag_specifications" +
                (id is null ? string.Empty : " WHERE rule_id = @id") + " ORDER BY rule_id, sort_order;";
            if (id is not null)
            {
                AddParameter(command, "@id", id);
            }

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var ruleId = reader.GetString(1);
                if (!specsByRule.TryGetValue(ruleId, out var list))
                {
                    specsByRule[ruleId] = list = [];
                }

                list.Add(new AutoTagSpecification(reader.GetString(2), reader.GetInt32(3) != 0, reader.GetString(4), reader.GetString(0)));
            }
        }

        var rules = new List<AutoTagRule>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, name, tags, remove_automatically, applies_to, is_enabled, created_utc, updated_utc FROM auto_tag_rules" +
                (id is null ? string.Empty : " WHERE id = @id") + " ORDER BY name COLLATE NOCASE ASC, id ASC;";
            if (id is not null)
            {
                AddParameter(command, "@id", id);
            }

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var ruleId = reader.GetString(0);
                rules.Add(new AutoTagRule(
                    ruleId,
                    reader.GetString(1),
                    TagNameList.Split(reader.GetString(2)),
                    reader.GetInt32(3) != 0,
                    AutoTagScopes.Normalize(reader.GetString(4)),
                    reader.GetInt32(5) != 0,
                    specsByRule.TryGetValue(ruleId, out var specs) ? specs : [],
                    ParseTimestamp(reader.GetString(6)),
                    ParseTimestamp(reader.GetString(7))));
            }
        }

        return rules;
    }

    private static async Task<int> Exec(DbConnection connection, DbTransaction? transaction, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            AddParameter(command, name, value);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
