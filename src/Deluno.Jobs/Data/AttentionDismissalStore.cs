using System.Globalization;
using Deluno.Infrastructure.Storage;

namespace Deluno.Jobs.Data;

/// <summary>
/// What the person has dismissed from the bell (#1098). A dismissal hides one item; it changes nothing about
/// the title or the problem, and it is forgotten as soon as the item stops being a problem, so the same
/// problem coming back later is a new row.
/// </summary>
public sealed class AttentionDismissalStore(IDelunoDatabaseConnectionFactory databases, TimeProvider timeProvider)
{
    public const string NoReleasePrefix = "no-release:";
    public const string HeldReleasePrefix = "held-release:";

    public const string ClientHistoryPrefix = "client-history:";

    /// <summary>A failed row in a download client's history (#1218), dismissed so Activity stops counting it.</summary>
    public static string ClientHistoryKey(string clientId, string itemId) => $"{ClientHistoryPrefix}{clientId}:{itemId}";

    public static string NoReleaseKey(string mediaType, string id) => $"{NoReleasePrefix}{mediaType}:{id}";

    /// <summary>A held release (#1159) is dismissed by its search-history row, so a later held search is a new item.</summary>
    public static string HeldReleaseKey(string historyId) => $"{HeldReleasePrefix}{historyId}";

    public const string HeldReleaseNamePrefix = "held-release-name:";

    /// <summary>
    /// The same release for the same title or episode (#1193). A feed watch that offers a dismissed release again
    /// starts a new search-history row, which the row key alone would call a new item; this key says it is not.
    /// </summary>
    public static string HeldReleaseNameKey(string mediaType, string id, string? episodeId, string releaseName)
        => $"{HeldReleaseNamePrefix}{mediaType}:{id}:{episodeId}:{releaseName.Trim().ToLowerInvariant()}";

    /// <summary>Whether a held release was dismissed, by its row or as the same release of the same title.</summary>
    public static bool IsHeldReleaseDismissed(IReadOnlySet<string> dismissed, string historyId, string mediaType, string id, string? episodeId, string releaseName)
        => dismissed.Contains(HeldReleaseKey(historyId)) || dismissed.Contains(HeldReleaseNameKey(mediaType, id, episodeId, releaseName));

    /// <summary>
    /// A title's dismissed release is forgotten once the title is held for a different release: that is something
    /// new, so it is offered. Titles with no held release now keep their dismissal.
    /// </summary>
    public async Task ForgetSupersededHeldReleasesAsync(IEnumerable<(string MediaType, string Id, string? EpisodeId, string ReleaseName)> held, CancellationToken cancellationToken)
    {
        var current = held.ToArray();
        var existing = await ListAsync(cancellationToken);
        var gone = new List<string>();
        foreach (var group in current.GroupBy(item => $"{HeldReleaseNamePrefix}{item.MediaType}:{item.Id}:{item.EpisodeId}:", StringComparer.OrdinalIgnoreCase))
        {
            var keep = group.Select(item => HeldReleaseNameKey(item.MediaType, item.Id, item.EpisodeId, item.ReleaseName)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            gone.AddRange(existing.Where(key => key.StartsWith(group.Key, StringComparison.OrdinalIgnoreCase) && !keep.Contains(key)));
        }

        if (gone.Count == 0)
        {
            return;
        }

        await using var connection = await databases.OpenConnectionAsync(DelunoDatabaseNames.Jobs, cancellationToken);
        foreach (var key in gone)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM attention_dismissals WHERE dismissal_key = @key;";
            Add(command, "@key", key);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task DismissAsync(string key, CancellationToken cancellationToken)
    {
        await using var connection = await databases.OpenConnectionAsync(DelunoDatabaseNames.Jobs, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO attention_dismissals (dismissal_key, dismissed_utc) VALUES (@key, @utc) ON CONFLICT (dismissal_key) DO NOTHING;";
        Add(command, "@key", key);
        Add(command, "@utc", timeProvider.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlySet<string>> ListAsync(CancellationToken cancellationToken)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var connection = await databases.OpenReadOnlyConnectionAsync(DelunoDatabaseNames.Jobs, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT dismissal_key FROM attention_dismissals;";
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            keys.Add(reader.GetString(0));
        }

        return keys;
    }

    /// <summary>
    /// Forgets every dismissal whose key is not in <paramref name="stillProblems"/>. Only keys that start with
    /// <paramref name="keyPrefix"/> are looked at, so one list's problems never forget another list's dismissals.
    /// </summary>
    public async Task ForgetExceptAsync(IReadOnlySet<string> stillProblems, CancellationToken cancellationToken, string keyPrefix = NoReleasePrefix)
    {
        var existing = await ListAsync(cancellationToken);
        var gone = existing.Where(key => key.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase) && !stillProblems.Contains(key)).ToArray();
        if (gone.Length == 0)
        {
            return;
        }

        await using var connection = await databases.OpenConnectionAsync(DelunoDatabaseNames.Jobs, cancellationToken);
        foreach (var key in gone)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM attention_dismissals WHERE dismissal_key = @key;";
            Add(command, "@key", key);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static void Add(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
