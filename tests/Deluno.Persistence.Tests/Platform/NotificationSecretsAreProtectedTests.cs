using Deluno.Infrastructure.Storage;
using Deluno.Notifications.Contracts;
using Deluno.Notifications.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Security;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Platform;

/// <summary>
/// #1364: a notification connection's address (a Discord or Slack token sits in its path) and settings (Telegram,
/// Pushover, Gotify and ntfy tokens) are stored protected, shown masked, kept when a save leaves them masked, and
/// protected on an install that saved them in clear.
/// </summary>
public sealed class NotificationSecretsAreProtectedTests
{
    private const string DiscordUrl = "https://discord.com/api/webhooks/1234567890/SECRET-discord-token-8c2d";
    private const string Config = "{\"botToken\":\"123456789:SECRET-bot\",\"chatId\":\"42\"}";

    private static readonly FixedTimeProvider Clock = new(DateTimeOffset.Parse("2026-10-19T02:00:00Z"));

    private static async Task<(SqliteNotificationRepository Repository, ISecretProtector Protector)> StartAsync(TestStorage storage)
    {
        await new PlatformSchemaInitializer(storage.Factory, new Deluno.Infrastructure.Storage.Migrations.SqliteDatabaseMigrator(storage.Factory, Clock), NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var protector = TestSecretProtection.Create(storage);
        return (new SqliteNotificationRepository(storage.Factory, Clock, protector), protector);
    }

    private static async Task<string> StoredAsync(TestStorage storage, string id)
    {
        await using var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Platform, CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT url || ' ' || COALESCE(config_json, '') FROM notification_webhooks WHERE id = @id;";
        command.Parameters.Add(new SqliteParameter("@id", id));
        return (string)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task A_saved_address_and_settings_are_stored_protected_listed_masked_and_still_sent_in_clear()
    {
        using var storage = TestStorage.Create();
        var (repository, _) = await StartAsync(storage);

        var made = await repository.CreateNotificationWebhookAsync(new CreateNotificationWebhookRequest("Chat", DiscordUrl, "all", true, NotificationProviders.Discord, Config), CancellationToken.None);
        var telegram = await repository.CreateNotificationWebhookAsync(new CreateNotificationWebhookRequest("Phone", "", "all", true, NotificationProviders.Telegram, Config), CancellationToken.None);

        var stored = await StoredAsync(storage, made.Id) + await StoredAsync(storage, telegram.Id);
        Assert.DoesNotContain("SECRET", stored, StringComparison.Ordinal);
        Assert.Contains("dp:v1:", stored, StringComparison.Ordinal);

        foreach (var shown in (await repository.ListNotificationWebhooksAsync(CancellationToken.None)).Append(made).Append(telegram))
        {
            Assert.DoesNotContain("SECRET", shown.Url + shown.ConfigJson, StringComparison.Ordinal);
        }

        var listedDiscord = (await repository.ListNotificationWebhooksAsync(CancellationToken.None)).Single(item => item.Id == made.Id);
        Assert.Equal("https://discord.com/" + NotificationSecrets.Mask, listedDiscord.Url);
        Assert.Contains("\"chatId\":\"42\"", listedDiscord.ConfigJson, StringComparison.Ordinal);

        var sending = (await repository.ListNotificationWebhooksWithSecretsAsync(CancellationToken.None)).Single(item => item.Id == made.Id);
        Assert.Equal(DiscordUrl, sending.Url);
        Assert.Equal(Config, sending.ConfigJson);
    }

    [Fact]
    public async Task A_save_that_leaves_the_address_and_tokens_masked_keeps_what_was_stored_and_a_new_value_replaces_it()
    {
        using var storage = TestStorage.Create();
        var (repository, _) = await StartAsync(storage);
        var made = await repository.CreateNotificationWebhookAsync(new CreateNotificationWebhookRequest("Chat", DiscordUrl, "all", true, NotificationProviders.Telegram, Config), CancellationToken.None);

        var listed = (await repository.ListNotificationWebhooksAsync(CancellationToken.None)).Single();
        await repository.UpdateNotificationWebhookAsync(made.Id, new UpdateNotificationWebhookRequest("Chat", listed.Url, "grab", true, NotificationProviders.Telegram, listed.ConfigJson), CancellationToken.None);

        var kept = (await repository.ListNotificationWebhooksWithSecretsAsync(CancellationToken.None)).Single();
        Assert.Equal(DiscordUrl, kept.Url);
        Assert.Contains("123456789:SECRET-bot", kept.ConfigJson, StringComparison.Ordinal);
        Assert.Equal("grab", kept.EventFilters);

        await repository.UpdateNotificationWebhookAsync(made.Id, new UpdateNotificationWebhookRequest("Chat", "https://discord.com/api/webhooks/9/NEW", "grab", true, NotificationProviders.Telegram, "{\"botToken\":\"NEW-bot\",\"chatId\":\"7\"}"), CancellationToken.None);
        var replaced = (await repository.ListNotificationWebhooksWithSecretsAsync(CancellationToken.None)).Single();
        Assert.Equal("https://discord.com/api/webhooks/9/NEW", replaced.Url);
        Assert.Equal("{\"botToken\":\"NEW-bot\",\"chatId\":\"7\"}", replaced.ConfigJson);
        Assert.DoesNotContain("SECRET", await StoredAsync(storage, made.Id), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_install_that_saved_addresses_and_tokens_in_clear_has_them_protected_once_and_they_still_send()
    {
        using var storage = TestStorage.Create();
        var (repository, protector) = await StartAsync(storage);
        await using (var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Platform, CancellationToken.None))
        {
            // What the previous build wrote: the address and the settings as plain text, one row with neither.
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO notification_webhooks (id, name, url, event_filters, is_enabled, created_utc, updated_utc, provider, config_json) VALUES " +
                $"('a', 'Discord on discord.com', '{DiscordUrl}', 'all', 1, '2026-10-01T00:00:00Z', '2026-10-01T00:00:00Z', 'discord', NULL), " +
                $"('b', 'Telegram', '', 'all', 1, '2026-10-01T00:00:00Z', '2026-10-01T00:00:00Z', 'telegram', '{Config}'), " +
                "('c', 'Webhook', '', 'all', 1, '2026-10-01T00:00:00Z', '2026-10-01T00:00:00Z', 'generic', NULL);";
            await command.ExecuteNonQueryAsync();
        }

        Assert.Equal(2, await repository.ProtectStoredSecretsAsync(CancellationToken.None));
        Assert.Equal(0, await repository.ProtectStoredSecretsAsync(CancellationToken.None));

        Assert.DoesNotContain("SECRET", await StoredAsync(storage, "a") + await StoredAsync(storage, "b"), StringComparison.Ordinal);
        Assert.True(protector.IsProtected((await StoredAsync(storage, "a")).Trim()));
        var sending = await repository.ListNotificationWebhooksWithSecretsAsync(CancellationToken.None);
        Assert.Equal(DiscordUrl, sending.Single(item => item.Id == "a").Url);
        Assert.Equal(Config, sending.Single(item => item.Id == "b").ConfigJson);
        Assert.Equal(string.Empty, sending.Single(item => item.Id == "c").Url);
        Assert.DoesNotContain("SECRET", string.Concat((await repository.ListNotificationWebhooksAsync(CancellationToken.None)).Select(item => item.Url + item.ConfigJson)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://discord.com/api/webhooks/1/SECRET", "discord.com")]
    [InlineData("http://127.0.0.1:8123/hook?token=SECRET", "127.0.0.1:8123")]
    [InlineData("not an address SECRET", "(no address)")]
    [InlineData(null, "(no address)")]
    public void A_log_line_names_the_host_and_never_the_path_or_query(string? url, string expected)
        => Assert.Equal(expected, NotificationSecrets.HostOnly(url));
}
