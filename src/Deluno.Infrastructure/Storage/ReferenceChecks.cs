using System.Data.Common;

namespace Deluno.Infrastructure.Storage;

/// <summary>The kinds of record another record can point at (#1316).</summary>
public enum ReferenceKind
{
    QualityProfile,
    PolicySet,
    Library,
    DestinationRule,
    DownloadClient,
    Indexer,
    IndexerProxy,
    ProcessorConnection,
    Tag,
    NotificationConnection
}

/// <summary>
/// #1316: the one check every create or update route runs on an id it is about to
/// store, so a record can never point at nothing or at a record of the wrong kind.
/// A quality profile, media plan or library also has a media type, and an id made
/// for the other type is refused too. The answer is a plain sentence naming the id.
/// </summary>
public static class ReferenceChecks
{
    /// <summary>Returns the sentence to show when <paramref name="id"/> is not a usable reference, or null when it is fine (or empty, which means "none").</summary>
    public static async Task<string?> FindProblemAsync(
        IDelunoDatabaseConnectionFactory connectionFactory,
        ReferenceKind kind,
        string? id,
        string? requiredMediaType,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        await using var connection = await connectionFactory.OpenReadOnlyConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        return await FindProblemAsync(connection, kind, id, requiredMediaType, cancellationToken);
    }

    public static async Task<string?> FindProblemAsync(
        DbConnection connection,
        ReferenceKind kind,
        string? id,
        string? requiredMediaType,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var trimmed = id.Trim();
        var (table, label, hasMediaType) = Describe(kind);
        using var command = connection.CreateCommand();
        command.CommandText = hasMediaType
            ? $"SELECT media_type FROM {table} WHERE id = @id COLLATE NOCASE LIMIT 1;"
            : $"SELECT '' FROM {table} WHERE id = @id COLLATE NOCASE LIMIT 1;";
        SqliteRecordHelpers.AddParameter(command, "@id", trimmed);
        var found = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (found is null)
        {
            return $"There is no {label} \"{trimmed}\". Choose one that exists.";
        }

        if (hasMediaType
            && !string.IsNullOrWhiteSpace(requiredMediaType)
            && !string.Equals(found, requiredMediaType.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return $"The {label} \"{trimmed}\" is made for {Plural(found)}, not {Plural(requiredMediaType.Trim())}. Choose one made for {Plural(requiredMediaType.Trim())}.";
        }

        return null;
    }

    /// <summary>Checks the id and, when it is not usable, records the sentence under <paramref name="field"/>.</summary>
    public static async Task AddProblemAsync(
        IDelunoDatabaseConnectionFactory connectionFactory,
        IDictionary<string, string[]> errors,
        string field,
        ReferenceKind kind,
        string? id,
        string? requiredMediaType,
        CancellationToken cancellationToken)
    {
        var problem = await FindProblemAsync(connectionFactory, kind, id, requiredMediaType, cancellationToken);
        if (problem is not null)
        {
            errors[field] = [problem];
        }
    }

    private static string Plural(string mediaType)
        => string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase) ? "TV" : mediaType.ToLowerInvariant();

    private static (string Table, string Label, bool HasMediaType) Describe(ReferenceKind kind) => kind switch
    {
        ReferenceKind.QualityProfile => ("quality_profiles", "quality profile", true),
        ReferenceKind.PolicySet => ("policy_sets", "media plan", true),
        ReferenceKind.Library => ("libraries", "library", true),
        ReferenceKind.DestinationRule => ("destination_rules", "destination rule", true),
        ReferenceKind.DownloadClient => ("download_clients", "download client", false),
        ReferenceKind.Indexer => ("indexer_sources", "indexer", false),
        ReferenceKind.IndexerProxy => ("indexer_proxies", "indexer proxy", false),
        ReferenceKind.ProcessorConnection => ("processor_connections", "processor connection", false),
        ReferenceKind.NotificationConnection => ("notification_webhooks", "notification connection", false),
        ReferenceKind.Tag => ("tags", "tag", false),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}
