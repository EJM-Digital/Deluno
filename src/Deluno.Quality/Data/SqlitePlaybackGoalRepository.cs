using System.Text.Json;
using Deluno.Infrastructure.Storage;
using Deluno.Quality.Playback;
using static Deluno.Infrastructure.Storage.SqliteRecordHelpers;

namespace Deluno.Quality.Data;

public sealed class SqlitePlaybackGoalRepository(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider) : IPlaybackGoalRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<PlaybackDeviceProfile>> ListDeviceProfilesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, capabilities_json, is_enabled, created_utc, updated_utc FROM playback_device_profiles ORDER BY name COLLATE NOCASE, id;";
        return await ReadProfilesAsync(command, cancellationToken);
    }

    public async Task<PlaybackDeviceProfile?> GetDeviceProfileAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, capabilities_json, is_enabled, created_utc, updated_utc FROM playback_device_profiles WHERE id = @id LIMIT 1;";
        AddParameter(command, "@id", id);
        return (await ReadProfilesAsync(command, cancellationToken)).FirstOrDefault();
    }

    public async Task<PlaybackDeviceProfile> CreateDeviceProfileAsync(CreatePlaybackDeviceProfileRequest request, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var item = NormalizeProfile(Guid.CreateVersion7().ToString("N"), request.Name, request.Capabilities, request.IsEnabled, now, now);
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO playback_device_profiles (id, name, capabilities_json, is_enabled, created_utc, updated_utc) VALUES (@id, @name, @capabilities, @enabled, @created, @updated);";
        BindProfile(command, item);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return item;
    }

    public async Task<PlaybackDeviceProfile?> UpdateDeviceProfileAsync(string id, UpdatePlaybackDeviceProfileRequest request, CancellationToken cancellationToken)
    {
        var existing = await GetDeviceProfileAsync(id, cancellationToken);
        if (existing is null) return null;
        var item = NormalizeProfile(existing.Id, request.Name ?? existing.Name, request.Capabilities ?? existing.Capabilities, request.IsEnabled, existing.CreatedUtc, timeProvider.GetUtcNow());
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE playback_device_profiles SET name = @name, capabilities_json = @capabilities, is_enabled = @enabled, updated_utc = @updated WHERE id = @id;";
        BindProfile(command, item);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 0 ? null : item;
    }

    public async Task<bool> DeleteDeviceProfileAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM playback_device_profiles WHERE id = @id;";
        AddParameter(command, "@id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<IReadOnlyList<PlaybackDeviceGroup>> ListDeviceGroupsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, mode, device_profile_ids_json, primary_device_profile_id, created_utc, updated_utc FROM playback_device_groups ORDER BY name COLLATE NOCASE, id;";
        return await ReadGroupsAsync(command, cancellationToken);
    }

    public async Task<PlaybackDeviceGroup?> GetDeviceGroupAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, mode, device_profile_ids_json, primary_device_profile_id, created_utc, updated_utc FROM playback_device_groups WHERE id = @id LIMIT 1;";
        AddParameter(command, "@id", id);
        return (await ReadGroupsAsync(command, cancellationToken)).FirstOrDefault();
    }

    public async Task<PlaybackDeviceGroup> CreateDeviceGroupAsync(CreatePlaybackDeviceGroupRequest request, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var item = NormalizeGroup(Guid.CreateVersion7().ToString("N"), request.Name, request.Mode, request.DeviceProfileIds, request.PrimaryDeviceProfileId, now, now);
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO playback_device_groups (id, name, mode, device_profile_ids_json, primary_device_profile_id, created_utc, updated_utc) VALUES (@id, @name, @mode, @profiles, @primary, @created, @updated);";
        BindGroup(command, item);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return item;
    }

    public async Task<PlaybackDeviceGroup?> UpdateDeviceGroupAsync(string id, UpdatePlaybackDeviceGroupRequest request, CancellationToken cancellationToken)
    {
        var existing = await GetDeviceGroupAsync(id, cancellationToken);
        if (existing is null) return null;
        var item = NormalizeGroup(existing.Id, request.Name ?? existing.Name, request.Mode ?? existing.Mode, request.DeviceProfileIds ?? existing.DeviceProfileIds, request.PrimaryDeviceProfileId ?? existing.PrimaryDeviceProfileId, existing.CreatedUtc, timeProvider.GetUtcNow());
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE playback_device_groups SET name = @name, mode = @mode, device_profile_ids_json = @profiles, primary_device_profile_id = @primary, updated_utc = @updated WHERE id = @id;";
        BindGroup(command, item);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 0 ? null : item;
    }

    public async Task<bool> DeleteDeviceGroupAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM playback_device_groups WHERE id = @id;";
        AddParameter(command, "@id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<IReadOnlyList<PlaybackGoalItem>> ListGoalsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, media_type, device_group_id, must_play, required_trait_ids_json, required_any_trait_groups_json, preferred_trait_ids_json, stop_when_trait_id, created_utc, updated_utc, forbidden_trait_ids_json FROM playback_goals ORDER BY name COLLATE NOCASE, id;";
        return await ReadGoalsAsync(command, cancellationToken);
    }

    public async Task<PlaybackGoalItem?> GetGoalAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, media_type, device_group_id, must_play, required_trait_ids_json, required_any_trait_groups_json, preferred_trait_ids_json, stop_when_trait_id, created_utc, updated_utc, forbidden_trait_ids_json FROM playback_goals WHERE id = @id LIMIT 1;";
        AddParameter(command, "@id", id);
        return (await ReadGoalsAsync(command, cancellationToken)).FirstOrDefault();
    }

    public async Task<PlaybackGoalItem> CreateGoalAsync(CreatePlaybackGoalRequest request, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var item = NormalizeGoal(Guid.CreateVersion7().ToString("N"), request.Name, request.MediaType, request.DeviceGroupId, request.MustPlay, request.RequiredTraitIds, request.RequiredAnyTraitGroups, request.PreferredTraitIds, request.StopWhenTraitId, request.ForbiddenTraitIds, now, now);
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO playback_goals (id, name, media_type, device_group_id, must_play, required_trait_ids_json, required_any_trait_groups_json, preferred_trait_ids_json, stop_when_trait_id, created_utc, updated_utc, forbidden_trait_ids_json) VALUES (@id, @name, @media, @group, @mustPlay, @required, @requiredAny, @preferred, @stopWhen, @created, @updated, @forbidden);";
        BindGoal(command, item);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return item;
    }

    public async Task<PlaybackGoalItem?> UpdateGoalAsync(string id, UpdatePlaybackGoalRequest request, CancellationToken cancellationToken)
    {
        var existing = await GetGoalAsync(id, cancellationToken);
        if (existing is null) return null;
        var item = NormalizeGoal(existing.Id, request.Name ?? existing.Name, request.MediaType ?? existing.MediaType, request.DeviceGroupId ?? existing.DeviceGroupId, request.MustPlay, request.RequiredTraitIds ?? existing.RequiredTraitIds, request.RequiredAnyTraitGroups ?? existing.RequiredAnyTraitGroups, request.PreferredTraitIds ?? existing.PreferredTraitIds, request.StopWhenTraitId ?? existing.StopWhenTraitId, request.ForbiddenTraitIds ?? existing.EffectiveForbiddenTraitIds, existing.CreatedUtc, timeProvider.GetUtcNow());
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE playback_goals SET name = @name, media_type = @media, device_group_id = @group, must_play = @mustPlay, required_trait_ids_json = @required, required_any_trait_groups_json = @requiredAny, preferred_trait_ids_json = @preferred, stop_when_trait_id = @stopWhen, updated_utc = @updated, forbidden_trait_ids_json = @forbidden WHERE id = @id;";
        BindGoal(command, item);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 0 ? null : item;
    }

    public async Task<bool> DeleteGoalAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM playback_goals WHERE id = @id;";
        AddParameter(command, "@id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static async Task<IReadOnlyList<PlaybackDeviceProfile>> ReadProfilesAsync(System.Data.Common.DbCommand command, CancellationToken cancellationToken)
    {
        var items = new List<PlaybackDeviceProfile>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new PlaybackDeviceProfile(
                reader.GetString(0),
                reader.GetString(1),
                Deserialize<List<PlaybackCapability>>(reader.IsDBNull(2) ? "[]" : reader.GetString(2)) ?? [],
                reader.GetInt64(3) == 1,
                ParseTimestamp(reader.GetString(4)),
                ParseTimestamp(reader.GetString(5))));
        }

        return items;
    }

    private static async Task<IReadOnlyList<PlaybackDeviceGroup>> ReadGroupsAsync(System.Data.Common.DbCommand command, CancellationToken cancellationToken)
    {
        var items = new List<PlaybackDeviceGroup>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new PlaybackDeviceGroup(
                reader.GetString(0),
                reader.GetString(1),
                PlaybackGoalModes.Normalize(reader.GetString(2)),
                Deserialize<List<string>>(reader.IsDBNull(3) ? "[]" : reader.GetString(3)) ?? [],
                reader.IsDBNull(4) ? null : reader.GetString(4),
                ParseTimestamp(reader.GetString(5)),
                ParseTimestamp(reader.GetString(6))));
        }

        return items;
    }

    private static async Task<IReadOnlyList<PlaybackGoalItem>> ReadGoalsAsync(System.Data.Common.DbCommand command, CancellationToken cancellationToken)
    {
        var items = new List<PlaybackGoalItem>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new PlaybackGoalItem(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4) == 1,
                Deserialize<List<string>>(reader.IsDBNull(5) ? "[]" : reader.GetString(5)) ?? [],
                Deserialize<List<List<string>>>(reader.IsDBNull(6) ? "[]" : reader.GetString(6))?.Cast<IReadOnlyList<string>>().ToArray() ?? [],
                Deserialize<List<string>>(reader.IsDBNull(7) ? "[]" : reader.GetString(7)) ?? [],
                reader.IsDBNull(8) ? null : reader.GetString(8),
                ParseTimestamp(reader.GetString(9)),
                ParseTimestamp(reader.GetString(10)),
                Deserialize<List<string>>(reader.IsDBNull(11) ? "[]" : reader.GetString(11)) ?? []));
        }

        return items;
    }

    private static T? Deserialize<T>(string json)
    {
        try { return JsonSerializer.Deserialize<T>(json, JsonOptions); }
        catch (JsonException) { return default; }
    }

    private static PlaybackDeviceProfile NormalizeProfile(string id, string? name, IReadOnlyList<PlaybackCapability>? capabilities, bool enabled, DateTimeOffset created, DateTimeOffset updated)
        => new(id, string.IsNullOrWhiteSpace(name) ? "Playback device" : name.Trim(), NormalizeCapabilities(capabilities, updated), enabled, created, updated);

    private static IReadOnlyList<PlaybackCapability> NormalizeCapabilities(IReadOnlyList<PlaybackCapability>? capabilities, DateTimeOffset updated)
        => (capabilities ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item.TraitId))
            .Select(item => new PlaybackCapability(
                item.TraitId.Trim().ToLowerInvariant(),
                PlaybackCapabilityStates.Normalize(item.State),
                PlaybackCapabilitySources.Normalize(item.Source),
                item.Confidence is null ? 1 : Math.Clamp(item.Confidence.Value, 0, 1),
                string.IsNullOrWhiteSpace(item.Detail) ? null : item.Detail.Trim(),
                item.LastConfirmedUtc ?? updated))
            .GroupBy(item => item.TraitId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .OrderBy(item => item.TraitId, StringComparer.Ordinal)
            .ToArray();

    private static PlaybackDeviceGroup NormalizeGroup(string id, string? name, string? mode, IReadOnlyList<string>? profileIds, string? primary, DateTimeOffset created, DateTimeOffset updated)
        => new(id, string.IsNullOrWhiteSpace(name) ? "Playback devices" : name.Trim(), PlaybackGoalModes.Normalize(mode), NormalizeIds(profileIds), NormalizeNullable(primary), created, updated);

    private static PlaybackGoalItem NormalizeGoal(string id, string? name, string? mediaType, string? groupId, bool mustPlay, IReadOnlyList<string>? required, IReadOnlyList<IReadOnlyList<string>>? requiredAny, IReadOnlyList<string>? preferred, string? stopWhen, IReadOnlyList<string>? forbidden, DateTimeOffset created, DateTimeOffset updated)
        => new(id, string.IsNullOrWhiteSpace(name) ? "Playback goal" : name.Trim(), NormalizeMediaType(mediaType), NormalizeNullable(groupId) ?? string.Empty, mustPlay, NormalizeIds(required), NormalizeGroups(requiredAny), NormalizeOrderedIds(preferred), NormalizeNullable(stopWhen), created, updated, NormalizeIds(forbidden));

    private static IReadOnlyList<string> NormalizeIds(IReadOnlyList<string>? values)
        => (values ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim().ToLowerInvariant()).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();

    private static IReadOnlyList<string> NormalizeOrderedIds(IReadOnlyList<string>? values)
        => (values ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim().ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToArray();

    private static IReadOnlyList<IReadOnlyList<string>> NormalizeGroups(IReadOnlyList<IReadOnlyList<string>>? values)
        => (values ?? []).Select(NormalizeIds).Where(group => group.Count > 0).ToArray();

    private static string? NormalizeNullable(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string NormalizeMediaType(string? value) => string.Equals(value?.Trim(), "tv", StringComparison.OrdinalIgnoreCase) || string.Equals(value?.Trim(), "series", StringComparison.OrdinalIgnoreCase) ? "tv" : "movies";

    private static void BindProfile(System.Data.Common.DbCommand command, PlaybackDeviceProfile item)
    {
        AddParameter(command, "@id", item.Id);
        AddParameter(command, "@name", item.Name);
        AddParameter(command, "@capabilities", JsonSerializer.Serialize(item.Capabilities, JsonOptions));
        AddParameter(command, "@enabled", item.IsEnabled ? 1 : 0);
        AddParameter(command, "@created", item.CreatedUtc.ToString("O"));
        AddParameter(command, "@updated", item.UpdatedUtc.ToString("O"));
    }

    private static void BindGroup(System.Data.Common.DbCommand command, PlaybackDeviceGroup item)
    {
        AddParameter(command, "@id", item.Id);
        AddParameter(command, "@name", item.Name);
        AddParameter(command, "@mode", item.Mode);
        AddParameter(command, "@profiles", JsonSerializer.Serialize(item.DeviceProfileIds, JsonOptions));
        AddParameter(command, "@primary", item.PrimaryDeviceProfileId);
        AddParameter(command, "@created", item.CreatedUtc.ToString("O"));
        AddParameter(command, "@updated", item.UpdatedUtc.ToString("O"));
    }

    private static void BindGoal(System.Data.Common.DbCommand command, PlaybackGoalItem item)
    {
        AddParameter(command, "@id", item.Id);
        AddParameter(command, "@name", item.Name);
        AddParameter(command, "@media", item.MediaType);
        AddParameter(command, "@group", item.DeviceGroupId);
        AddParameter(command, "@mustPlay", item.MustPlay ? 1 : 0);
        AddParameter(command, "@required", JsonSerializer.Serialize(item.RequiredTraitIds, JsonOptions));
        AddParameter(command, "@requiredAny", JsonSerializer.Serialize(item.RequiredAnyTraitGroups, JsonOptions));
        AddParameter(command, "@preferred", JsonSerializer.Serialize(item.PreferredTraitIds, JsonOptions));
        AddParameter(command, "@stopWhen", item.StopWhenTraitId);
        AddParameter(command, "@created", item.CreatedUtc.ToString("O"));
        AddParameter(command, "@updated", item.UpdatedUtc.ToString("O"));
        AddParameter(command, "@forbidden", JsonSerializer.Serialize(item.EffectiveForbiddenTraitIds, JsonOptions));
    }
}
