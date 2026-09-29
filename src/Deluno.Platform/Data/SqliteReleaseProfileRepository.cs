using System.Text.Json;
using Deluno.Infrastructure.Storage;
using Deluno.Platform.Contracts;
using Deluno.Quality;
using static Deluno.Infrastructure.Storage.SqliteRecordHelpers;

namespace Deluno.Platform.Data;

public sealed class SqliteReleaseProfileRepository(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider) : IReleaseProfileRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<ReleaseProfileItem>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = SelectSql + " ORDER BY position ASC, lower(trim(tag_name)) ASC, name COLLATE NOCASE ASC;";
        return await ReadManyAsync(command, cancellationToken);
    }

    public async Task<ReleaseProfileItem?> GetAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = SelectSql + " WHERE id = @id LIMIT 1;";
        AddParameter(command, "@id", id);
        return (await ReadManyAsync(command, cancellationToken)).FirstOrDefault();
    }

    public async Task<IReadOnlyList<ReleaseProfileItem>> ListApplicableAsync(
        IReadOnlyList<string>? tagNames,
        CancellationToken cancellationToken)
    {
        var names = tagNames?
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(NormalizeTagName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];

        var all = await ListAsync(cancellationToken);
        var matching = all
            .Where(profile => profile.AppliesToTags.Count == 0
                || profile.AppliesToTags.Any(tag => names.Contains(tag, StringComparer.OrdinalIgnoreCase)))
            .ToArray();

        if (await GetRuleModeAsync(cancellationToken) != ReleaseRuleModes.FirstMatch)
        {
            return matching;
        }

        // #995: the top rule that matches the title's tags is the only one used;
        // the rule with no tag is the default and only speaks when no tagged rule
        // matches. A switched-off rule is skipped, not chosen and then ignored.
        // The list is already in position order.
        var winner = matching.FirstOrDefault(profile => profile.Enabled && profile.AppliesToTags.Count > 0)
            ?? matching.FirstOrDefault(profile => profile.Enabled && profile.AppliesToTags.Count == 0);
        return winner is null ? [] : [winner];
    }

    public async Task<string> GetRuleModeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT setting_value FROM system_settings WHERE setting_key = @key;";
        AddParameter(command, "@key", ReleaseRuleModes.SettingKey);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return ReleaseRuleModes.Normalize(value as string);
    }

    public async Task<string> SetRuleModeAsync(string? mode, CancellationToken cancellationToken)
    {
        var normalized = ReleaseRuleModes.Normalize(mode);
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO system_settings (setting_key, setting_value, updated_utc)
            VALUES (@key, @value, @updatedUtc)
            ON CONFLICT(setting_key) DO UPDATE SET
                setting_value = excluded.setting_value,
                updated_utc = excluded.updated_utc;
            """;
        AddParameter(command, "@key", ReleaseRuleModes.SettingKey);
        AddParameter(command, "@value", normalized);
        AddParameter(command, "@updatedUtc", timeProvider.GetUtcNow().ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return normalized;
    }

    public async Task<IReadOnlyList<ReleaseProfileItem>> ReorderAsync(
        IReadOnlyList<string> orderedIds,
        CancellationToken cancellationToken)
    {
        var current = await ListAsync(cancellationToken);
        var known = current.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var next = orderedIds
            .Where(known.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Concat(current.Select(item => item.Id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        for (var index = 0; index < next.Length; index++)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE release_profiles SET position = @position WHERE id = @id;";
            AddParameter(command, "@position", index);
            AddParameter(command, "@id", next[index]);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return await ListAsync(cancellationToken);
    }

    public async Task<ReleaseProfileItem> CreateAsync(
        CreateReleaseProfileRequest request,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var existing = await ListAsync(cancellationToken);
        var item = Normalize(
            Guid.CreateVersion7().ToString("N"),
            request.Name,
            request.TagNames ?? (request.TagName is null ? null : [request.TagName]),
            request.PreferredProtocol,
            request.UsenetDelayMinutes,
            request.TorrentDelayMinutes,
            request.MustContain,
            request.MustNotContain,
            request.PreferredTerms,
            now,
            now,
            request.Enabled ?? true,
            request.IndexerIds,
            request.BypassIfHighestQuality ?? false,
            existing.Count == 0 ? 0 : existing.Max(profile => profile.Position) + 1);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO release_profiles (
                id, name, tag_name, preferred_protocol, usenet_delay_minutes,
                torrent_delay_minutes, must_contain, must_not_contain,
                preferred_terms_json, created_utc, updated_utc,
                enabled, indexer_ids_json, bypass_if_highest_quality,
                tag_names, position
            )
            VALUES (
                @id, @name, @tagName, @preferredProtocol, @usenetDelay,
                @torrentDelay, @mustContain, @mustNotContain,
                @preferredTerms, @createdUtc, @updatedUtc,
                @enabled, @indexerIds, @bypassIfHighestQuality,
                @tagNames, @position
            );
            """;
        Bind(command, item);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return item;
    }

    public async Task<ReleaseProfileItem?> UpdateAsync(
        string id,
        UpdateReleaseProfileRequest request,
        CancellationToken cancellationToken)
    {
        var existing = await GetAsync(id, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        var item = Normalize(
            existing.Id,
            request.Name ?? existing.Name,
            request.TagNames ?? (request.TagName is null ? existing.AppliesToTags : [request.TagName]),
            request.PreferredProtocol ?? existing.PreferredProtocol,
            request.UsenetDelayMinutes ?? existing.UsenetDelayMinutes,
            request.TorrentDelayMinutes ?? existing.TorrentDelayMinutes,
            request.MustContain ?? existing.MustContain,
            request.MustNotContain ?? existing.MustNotContain,
            request.PreferredTerms ?? existing.PreferredTerms,
            existing.CreatedUtc,
            now,
            request.Enabled ?? existing.Enabled,
            request.IndexerIds ?? existing.IndexerIds,
            request.BypassIfHighestQuality ?? existing.BypassIfHighestQuality,
            existing.Position);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE release_profiles
            SET name = @name,
                tag_name = @tagName,
                tag_names = @tagNames,
                preferred_protocol = @preferredProtocol,
                usenet_delay_minutes = @usenetDelay,
                torrent_delay_minutes = @torrentDelay,
                must_contain = @mustContain,
                must_not_contain = @mustNotContain,
                preferred_terms_json = @preferredTerms,
                updated_utc = @updatedUtc,
                enabled = @enabled,
                indexer_ids_json = @indexerIds,
                bypass_if_highest_quality = @bypassIfHighestQuality
            WHERE id = @id;
            """;
        Bind(command, item);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 0 ? null : item;
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM release_profiles WHERE id = @id;";
        AddParameter(command, "@id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static readonly string SelectSql =
        "SELECT id, name, tag_name, preferred_protocol, usenet_delay_minutes, torrent_delay_minutes, "
        + "must_contain, must_not_contain, preferred_terms_json, created_utc, updated_utc, "
        + "enabled, indexer_ids_json, bypass_if_highest_quality, tag_names, position "
        + "FROM release_profiles";

    private static async Task<IReadOnlyList<ReleaseProfileItem>> ReadManyAsync(
        System.Data.Common.DbCommand command,
        CancellationToken cancellationToken)
    {
        var items = new List<ReleaseProfileItem>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            IReadOnlyList<ReleaseTermScore> terms;
            try
            {
                terms = JsonSerializer.Deserialize<List<ReleaseTermScore>>(
                            reader.IsDBNull(8) ? "[]" : reader.GetString(8),
                            JsonOptions)
                        ?? [];
            }
            catch (JsonException)
            {
                terms = [];
            }

            IReadOnlyList<string> indexerIds;
            try
            {
                indexerIds = JsonSerializer.Deserialize<List<string>>(
                                 reader.IsDBNull(12) ? "[]" : reader.GetString(12),
                                 JsonOptions)
                             ?? [];
            }
            catch (JsonException)
            {
                indexerIds = [];
            }

            var tagNames = SplitTags(reader.IsDBNull(14) ? string.Empty : reader.GetString(14));
            var tagName = reader.GetString(2);
            if (tagNames.Count == 0 && !string.IsNullOrWhiteSpace(tagName))
            {
                tagNames = [tagName.Trim()];
            }

            items.Add(new ReleaseProfileItem(
                reader.GetString(0),
                reader.GetString(1),
                tagName,
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetString(6),
                reader.GetString(7),
                terms,
                ParseTimestamp(reader.GetString(9)),
                ParseTimestamp(reader.GetString(10)),
                reader.GetInt32(11) != 0,
                indexerIds,
                reader.GetInt32(13) != 0,
                tagNames,
                reader.GetInt32(15)));
        }

        return items;
    }

    private static ReleaseProfileItem Normalize(
        string id,
        string? name,
        IReadOnlyList<string>? tagNames,
        string? preferredProtocol,
        int? usenetDelayMinutes,
        int? torrentDelayMinutes,
        string? mustContain,
        string? mustNotContain,
        IReadOnlyList<ReleaseTermScore>? preferredTerms,
        DateTimeOffset createdUtc,
        DateTimeOffset updatedUtc,
        bool enabled,
        IReadOnlyList<string>? indexerIds,
        bool bypassIfHighestQuality,
        int position)
    {
        var tags = NormalizeTagNames(tagNames);
        return new(
            id,
            string.IsNullOrWhiteSpace(name) ? "Release profile" : name.Trim(),
            tags.Count == 0 ? string.Empty : tags[0],
            NormalizeProtocol(preferredProtocol),
            Math.Max(0, usenetDelayMinutes ?? 0),
            Math.Max(0, torrentDelayMinutes ?? 0),
            NormalizeTerms(mustContain),
            NormalizeTerms(mustNotContain),
            NormalizePreferredTerms(preferredTerms),
            createdUtc,
            updatedUtc,
            enabled,
            NormalizeIndexerIds(indexerIds),
            bypassIfHighestQuality,
            tags,
            position);
    }

    private static IReadOnlyList<string> NormalizeIndexerIds(IReadOnlyList<string>? indexerIds)
        => (indexerIds ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string NormalizeTagName(string? value) => value?.Trim() ?? string.Empty;

    private static IReadOnlyList<string> NormalizeTagNames(IReadOnlyList<string>? values)
        => (values ?? [])
            .SelectMany(value => SplitTags(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IReadOnlyList<string> SplitTags(string? csv)
        => (csv ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();

    private static string NormalizeProtocol(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "usenet" => "usenet",
            "torrent" => "torrent",
            _ => "any"
        };

    private static string NormalizeTerms(string? value)
        => string.Join(", ", ReleaseTermPattern.Split(value));

    private static IReadOnlyList<ReleaseTermScore> NormalizePreferredTerms(IReadOnlyList<ReleaseTermScore>? terms)
        => (terms ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item.Term) && item.Score is >= -10000 and <= 10000)
            .Select(item => new ReleaseTermScore(item.Term.Trim(), item.Score))
            .GroupBy(item => item.Term, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .OrderBy(item => item.Term, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static void Bind(System.Data.Common.DbCommand command, ReleaseProfileItem item)
    {
        AddParameter(command, "@id", item.Id);
        AddParameter(command, "@name", item.Name);
        AddParameter(command, "@tagName", item.TagName);
        AddParameter(command, "@tagNames", string.Join(", ", item.AppliesToTags));
        AddParameter(command, "@position", item.Position);
        AddParameter(command, "@preferredProtocol", item.PreferredProtocol);
        AddParameter(command, "@usenetDelay", item.UsenetDelayMinutes);
        AddParameter(command, "@torrentDelay", item.TorrentDelayMinutes);
        AddParameter(command, "@mustContain", item.MustContain);
        AddParameter(command, "@mustNotContain", item.MustNotContain);
        AddParameter(command, "@preferredTerms", JsonSerializer.Serialize(item.PreferredTerms, JsonOptions));
        AddParameter(command, "@createdUtc", item.CreatedUtc.ToString("O"));
        AddParameter(command, "@updatedUtc", item.UpdatedUtc.ToString("O"));
        AddParameter(command, "@enabled", item.Enabled ? 1 : 0);
        AddParameter(command, "@indexerIds", JsonSerializer.Serialize(item.IndexerIds, JsonOptions));
        AddParameter(command, "@bypassIfHighestQuality", item.BypassIfHighestQuality ? 1 : 0);
    }
}
