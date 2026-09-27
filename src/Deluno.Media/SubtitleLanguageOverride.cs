using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using static Deluno.Infrastructure.Storage.SqliteRecordHelpers;

namespace Deluno.Media;

/// <summary>
/// One title's own subtitle-language override (#843) — languages and mode,
/// exactly the shape <c>UpdateLibrarySubtitlesRequest</c> already uses, so
/// resolving one against the other is never comparing two different
/// vocabularies.
/// </summary>
public sealed record SubtitleLanguageOverride(IReadOnlyList<string> Languages, string Mode);

public interface IMediaSubtitleOverrideRepository
{
    /// <summary>The override for one title, or null when it has none and follows its library.</summary>
    Task<SubtitleLanguageOverride?> GetAsync(MediaKind kind, string mediaId, CancellationToken cancellationToken);

    /// <summary>Sets or replaces the override.</summary>
    Task SetAsync(MediaKind kind, string mediaId, SubtitleLanguageOverride @override, CancellationToken cancellationToken);

    /// <summary>Removes the override, so the title goes back to its library's setting.</summary>
    Task ClearAsync(MediaKind kind, string mediaId, CancellationToken cancellationToken);
}

/// <summary>
/// Resolves a title's effective subtitle languages and mode: its own
/// override when it has set one, its library's setting otherwise (#843).
/// A pure function on purpose — the same resolution has to agree wherever it
/// is asked, and a rule that lived only inside the search job would leave the
/// detail drawer showing one answer while the search acted on another.
/// </summary>
public static class SubtitleLanguagePreference
{
    public static (IReadOnlyList<string> Languages, string Mode) Resolve(
        IReadOnlyList<string>? libraryLanguages,
        string? libraryMode,
        SubtitleLanguageOverride? @override)
        => @override is { Languages.Count: > 0 }
            ? (@override.Languages, @override.Mode)
            : (libraryLanguages ?? [], libraryMode ?? "all");
}

public sealed class SqliteMediaSubtitleOverrideRepository(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider) : IMediaSubtitleOverrideRepository
{
    public async Task<SubtitleLanguageOverride?> GetAsync(
        MediaKind kind, string mediaId, CancellationToken cancellationToken)
    {
        var map = MediaTableMap.For(kind);
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            map.DatabaseName, cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT languages, mode FROM {map.SubtitleOverrideTable} WHERE {map.TagMediaIdColumn} = @mediaId;
            """;
        AddParameter(command, "@mediaId", mediaId);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var languages = reader.GetString(0)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new SubtitleLanguageOverride(languages, reader.GetString(1));
    }

    public async Task SetAsync(
        MediaKind kind, string mediaId, SubtitleLanguageOverride @override, CancellationToken cancellationToken)
    {
        var map = MediaTableMap.For(kind);
        var now = timeProvider.GetUtcNow();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            map.DatabaseName, cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO {map.SubtitleOverrideTable} ({map.TagMediaIdColumn}, languages, mode, updated_utc)
            VALUES (@mediaId, @languages, @mode, @now)
            ON CONFLICT ({map.TagMediaIdColumn}) DO UPDATE SET
                languages = excluded.languages,
                mode = excluded.mode,
                updated_utc = excluded.updated_utc;
            """;
        AddParameter(command, "@mediaId", mediaId);
        AddParameter(command, "@languages", string.Join(',', @override.Languages));
        AddParameter(command, "@mode", @override.Mode);
        AddParameter(command, "@now", now.ToString("O"));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ClearAsync(MediaKind kind, string mediaId, CancellationToken cancellationToken)
    {
        var map = MediaTableMap.For(kind);
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            map.DatabaseName, cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM {map.SubtitleOverrideTable} WHERE {map.TagMediaIdColumn} = @mediaId;";
        AddParameter(command, "@mediaId", mediaId);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
