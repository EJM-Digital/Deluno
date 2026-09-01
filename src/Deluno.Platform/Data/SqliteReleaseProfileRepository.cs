using System.Text.Json;
using Deluno.Infrastructure.Storage;
using Deluno.Platform.Contracts;
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
        command.CommandText = SelectSql + " ORDER BY lower(trim(tag_name)) ASC, name COLLATE NOCASE ASC;";
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

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        if (names.Length == 0)
        {
            command.CommandText = SelectSql + " WHERE trim(tag_name) = '' ORDER BY id;";
        }
        else
        {
            var parameters = new string[names.Length];
            for (var index = 0; index < names.Length; index++)
            {
                parameters[index] = $"@tag{index}";
                AddParameter(command, parameters[index], names[index]);
            }

            command.CommandText = SelectSql + $" WHERE trim(tag_name) = '' OR lower(trim(tag_name)) IN ({string.Join(", ", parameters)}) ORDER BY lower(trim(tag_name)) ASC, id ASC;";
        }

        return await ReadManyAsync(command, cancellationToken);
    }

    public async Task<ReleaseProfileItem> CreateAsync(
        CreateReleaseProfileRequest request,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var item = Normalize(
            Guid.CreateVersion7().ToString("N"),
            request.Name,
            request.TagName,
            request.PreferredProtocol,
            request.UsenetDelayMinutes,
            request.TorrentDelayMinutes,
            request.MustContain,
            request.MustNotContain,
            request.PreferredTerms,
            now,
            now);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO release_profiles (
                id, name, tag_name, preferred_protocol, usenet_delay_minutes,
                torrent_delay_minutes, must_contain, must_not_contain,
                preferred_terms_json, created_utc, updated_utc
            )
            VALUES (
                @id, @name, @tagName, @preferredProtocol, @usenetDelay,
                @torrentDelay, @mustContain, @mustNotContain,
                @preferredTerms, @createdUtc, @updatedUtc
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
            request.TagName ?? existing.TagName,
            request.PreferredProtocol ?? existing.PreferredProtocol,
            request.UsenetDelayMinutes ?? existing.UsenetDelayMinutes,
            request.TorrentDelayMinutes ?? existing.TorrentDelayMinutes,
            request.MustContain ?? existing.MustContain,
            request.MustNotContain ?? existing.MustNotContain,
            request.PreferredTerms ?? existing.PreferredTerms,
            existing.CreatedUtc,
            now);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE release_profiles
            SET name = @name,
                tag_name = @tagName,
                preferred_protocol = @preferredProtocol,
                usenet_delay_minutes = @usenetDelay,
                torrent_delay_minutes = @torrentDelay,
                must_contain = @mustContain,
                must_not_contain = @mustNotContain,
                preferred_terms_json = @preferredTerms,
                updated_utc = @updatedUtc
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
        + "must_contain, must_not_contain, preferred_terms_json, created_utc, updated_utc "
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

            items.Add(new ReleaseProfileItem(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetString(6),
                reader.GetString(7),
                terms,
                ParseTimestamp(reader.GetString(9)),
                ParseTimestamp(reader.GetString(10))));
        }

        return items;
    }

    private static ReleaseProfileItem Normalize(
        string id,
        string? name,
        string? tagName,
        string? preferredProtocol,
        int? usenetDelayMinutes,
        int? torrentDelayMinutes,
        string? mustContain,
        string? mustNotContain,
        IReadOnlyList<ReleaseTermScore>? preferredTerms,
        DateTimeOffset createdUtc,
        DateTimeOffset updatedUtc)
        => new(
            id,
            string.IsNullOrWhiteSpace(name) ? "Release profile" : name.Trim(),
            NormalizeTagName(tagName),
            NormalizeProtocol(preferredProtocol),
            Math.Max(0, usenetDelayMinutes ?? 0),
            Math.Max(0, torrentDelayMinutes ?? 0),
            NormalizeTerms(mustContain),
            NormalizeTerms(mustNotContain),
            NormalizePreferredTerms(preferredTerms),
            createdUtc,
            updatedUtc);

    private static string NormalizeTagName(string? value) => value?.Trim() ?? string.Empty;

    private static string NormalizeProtocol(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "usenet" => "usenet",
            "torrent" => "torrent",
            _ => "any"
        };

    private static string NormalizeTerms(string? value)
        => string.Join(", ", SplitTerms(value));

    private static IReadOnlyList<ReleaseTermScore> NormalizePreferredTerms(IReadOnlyList<ReleaseTermScore>? terms)
        => (terms ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item.Term) && item.Score is >= -10000 and <= 10000)
            .Select(item => new ReleaseTermScore(item.Term.Trim(), item.Score))
            .GroupBy(item => item.Term, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .OrderBy(item => item.Term, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IReadOnlyList<string> SplitTerms(string? value)
        => (value ?? string.Empty)
            .Split(['\r', '\n', ','], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(item => item.Length <= 100)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static void Bind(System.Data.Common.DbCommand command, ReleaseProfileItem item)
    {
        AddParameter(command, "@id", item.Id);
        AddParameter(command, "@name", item.Name);
        AddParameter(command, "@tagName", item.TagName);
        AddParameter(command, "@preferredProtocol", item.PreferredProtocol);
        AddParameter(command, "@usenetDelay", item.UsenetDelayMinutes);
        AddParameter(command, "@torrentDelay", item.TorrentDelayMinutes);
        AddParameter(command, "@mustContain", item.MustContain);
        AddParameter(command, "@mustNotContain", item.MustNotContain);
        AddParameter(command, "@preferredTerms", JsonSerializer.Serialize(item.PreferredTerms, JsonOptions));
        AddParameter(command, "@createdUtc", item.CreatedUtc.ToString("O"));
        AddParameter(command, "@updatedUtc", item.UpdatedUtc.ToString("O"));
    }
}
