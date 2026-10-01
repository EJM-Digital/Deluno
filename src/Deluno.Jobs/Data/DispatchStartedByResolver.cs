using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using static Deluno.Infrastructure.Storage.SqliteRecordHelpers;

namespace Deluno.Jobs.Data;

/// <summary>
/// Turns what a caller knows about a download into the reason stored on its dispatch (#1098).
/// Every answer comes from a record Deluno already holds, never a guess:
/// <list type="bullet">
/// <item>a person, Seerr, a list or an upgrade search is stored as the caller says;</item>
/// <item>"inherit" (a re-grab after a recovery, a refused release, a lost file) keeps the reason
/// of the title's last download, and stays empty when that one had none;</item>
/// <item>"missing" becomes Seerr or a list's provider name when the title has never been imported
/// and Deluno's own record says that request or list brought it in; otherwise it stays missing.</item>
/// </list>
/// </summary>
public static class DispatchStartedByResolver
{
    public static async Task<(string? Reason, string? Detail)> ResolveAsync(
        IDelunoDatabaseConnectionFactory databaseConnectionFactory,
        string? requested,
        string mediaType,
        string entityType,
        string entityId,
        string? titleEntityId,
        CancellationToken cancellationToken)
    {
        var word = requested?.Trim().ToLowerInvariant();
        if (word is null)
        {
            return (null, null);
        }

        try
        {
            if (word == DownloadStartedBy.Inherit)
            {
                return await LastReasonAsync(databaseConnectionFactory, entityType, entityId, cancellationToken);
            }

            if (word != DownloadStartedBy.Missing)
            {
                return (DownloadStartedBy.Normalize(word), null);
            }

            if (await HasBeenImportedAsync(databaseConnectionFactory, entityType, entityId, cancellationToken))
            {
                return (DownloadStartedBy.Missing, null);
            }

            var titleId = string.IsNullOrWhiteSpace(titleEntityId) ? entityId : titleEntityId;
            return await OriginAsync(databaseConnectionFactory, mediaType, titleId, cancellationToken)
                ?? (DownloadStartedBy.Missing, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Not being able to tell is never a reason to lose the dispatch: it simply has no reason.
            return (null, null);
        }
    }

    private static async Task<(string?, string?)> LastReasonAsync(
        IDelunoDatabaseConnectionFactory factory,
        string entityType,
        string entityId,
        CancellationToken cancellationToken)
    {
        await using var connection = await factory.OpenReadOnlyConnectionAsync(DelunoDatabaseNames.Jobs, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT started_by, started_by_detail FROM download_dispatches
            WHERE entity_type = @entityType AND entity_id = @entityId AND started_by IS NOT NULL
            ORDER BY created_utc DESC LIMIT 1;
            """;
        AddParameter(command, "@entityType", entityType);
        AddParameter(command, "@entityId", entityId);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1))
            : (null, null);
    }

    private static async Task<bool> HasBeenImportedAsync(
        IDelunoDatabaseConnectionFactory factory,
        string entityType,
        string entityId,
        CancellationToken cancellationToken)
    {
        await using var connection = await factory.OpenReadOnlyConnectionAsync(DelunoDatabaseNames.Jobs, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT 1 FROM download_dispatches WHERE entity_type = @entityType AND entity_id = @entityId AND import_status = 'imported' LIMIT 1;";
        AddParameter(command, "@entityType", entityType);
        AddParameter(command, "@entityId", entityId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<(string, string?)?> OriginAsync(
        IDelunoDatabaseConnectionFactory factory,
        string mediaType,
        string titleId,
        CancellationToken cancellationToken)
    {
        await using var connection = await factory.OpenReadOnlyConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);

        using (var seerr = connection.CreateCommand())
        {
            seerr.CommandText =
                "SELECT 1 FROM seerr_acquisitions WHERE media_type = @mediaType AND entity_id = @entityId AND status = 'added' LIMIT 1;";
            AddParameter(seerr, "@mediaType", mediaType);
            AddParameter(seerr, "@entityId", titleId);
            if (await seerr.ExecuteScalarAsync(cancellationToken) is not null)
            {
                return (DownloadStartedBy.Seerr, null);
            }
        }

        using var list = connection.CreateCommand();
        list.CommandText =
            """
            SELECT provider FROM intake_title_origins
            WHERE media_type = @mediaType AND entity_id = @entityId AND dropped_utc IS NULL
            ORDER BY first_seen_utc LIMIT 1;
            """;
        AddParameter(list, "@mediaType", mediaType);
        AddParameter(list, "@entityId", titleId);
        var provider = await list.ExecuteScalarAsync(cancellationToken);
        return provider is null or DBNull ? null : (DownloadStartedBy.List, ListProviderFamilies.KeyFor(Convert.ToString(provider)) == "other" ? null : ListProviderFamilies.NameFor(Convert.ToString(provider)));
    }
}
