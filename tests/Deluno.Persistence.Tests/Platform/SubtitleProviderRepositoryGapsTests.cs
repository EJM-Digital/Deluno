using Deluno.Security;
using Microsoft.AspNetCore.DataProtection;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Media;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Platform;

/// <summary>Subtitle provider rows with nothing or something odd stored, and the rules that read them. #1268.</summary>
public sealed class SubtitleProviderRepositoryGapsTests
{
    private static async Task<(TestStorage Storage, SqliteSubtitleProviderRepository Repository)> OpenAsync()
    {
        var storage = TestStorage.Create();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T09:00:00Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, time),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        return (storage, new SqliteSubtitleProviderRepository(storage.Factory, time, TestSecretProtection.Create(storage)));
    }

    [Fact]
    public async Task A_row_with_a_login_and_a_key_reads_them_back_and_a_row_with_neither_reads_none()
    {
        var (storage, repository) = await OpenAsync();
        using var _ = storage;

        await repository.SaveAsync("opensubtitles", "OpenSubtitles",
            new SaveSubtitleProviderRequest("opensubtitles", "someone", "secret", "key", 1, true), CancellationToken.None);
        await repository.SaveAsync("gestdown", "Gestdown",
            new SaveSubtitleProviderRequest("gestdown", null, null, null, 2, false), CancellationToken.None);
        await repository.SaveAsync("subdl", "SubDL",
            new SaveSubtitleProviderRequest("subdl", null, null, "only-a-key", 3, true), CancellationToken.None);

        var rows = (await repository.ListAsync(CancellationToken.None)).ToDictionary(row => row.ProviderKey);
        Assert.Equal("secret", rows["opensubtitles"].Secret);
        Assert.Equal("key", rows["opensubtitles"].ApiKey);
        Assert.Null(rows["gestdown"].Secret);
        Assert.Null(rows["gestdown"].ApiKey);
        Assert.False(rows["gestdown"].IsEnabled);
        Assert.False(rows["gestdown"].SecretUnreadable);
        Assert.Null(rows["subdl"].Secret);
        Assert.Equal("only-a-key", rows["subdl"].ApiKey);
    }

    [Fact]
    public async Task A_stored_failure_that_cannot_be_read_is_dropped_rather_than_breaking_the_list()
    {
        var (storage, repository) = await OpenAsync();
        using var _ = storage;
        await repository.SaveAsync("gestdown", "Gestdown",
            new SaveSubtitleProviderRequest("gestdown", null, null, null, 1, true), CancellationToken.None);

        await using (var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Platform, CancellationToken.None))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE subtitle_providers SET last_health_failure_json = '{not json';";
            await command.ExecuteNonQueryAsync();
        }

        var row = Assert.Single(await repository.ListAsync(CancellationToken.None));
        Assert.Null(row.LastHealthFailure);
    }

    [Fact]
    public void A_provider_is_askable_only_when_on_readable_and_not_inside_a_rate_limit()
    {
        var now = DateTimeOffset.Parse("2026-04-29T09:00:00Z");
        SubtitleProviderConnection Row(bool enabled, bool unreadable, DateTimeOffset? until) => new(
            "id", "gestdown", "Gestdown", null, null, null, 1, enabled, "untested", null, null, null, 0, until, null, now, now)
        {
            SecretUnreadable = unreadable
        };

        Assert.True(Row(true, false, null).IsAskable(now));
        Assert.True(Row(true, false, now).IsAskable(now));
        Assert.True(Row(true, false, now.AddMinutes(-1)).IsAskable(now));
        Assert.False(Row(true, false, now.AddMinutes(1)).IsAskable(now));
        Assert.False(Row(false, false, null).IsAskable(now));
        Assert.False(Row(true, true, null).IsAskable(now));
    }

    [Fact]
    public void A_titles_own_languages_win_over_the_librarys_only_when_it_names_some()
    {
        var own = SubtitleLanguagePreference.Resolve(["en"], "all", new SubtitleLanguageOverride(["fr"], "first"));
        Assert.Equal(["fr"], own.Languages);
        Assert.Equal("first", own.Mode);

        var emptyOverride = SubtitleLanguagePreference.Resolve(["en"], "all", new SubtitleLanguageOverride([], "first"));
        Assert.Equal(["en"], emptyOverride.Languages);
        Assert.Equal("all", emptyOverride.Mode);

        var none = SubtitleLanguagePreference.Resolve(["en"], "first", null);
        Assert.Equal(["en"], none.Languages);
        Assert.Equal("first", none.Mode);

        var nothing = SubtitleLanguagePreference.Resolve(null, null, null);
        Assert.Empty(nothing.Languages);
        Assert.Equal("all", nothing.Mode);
    }

    [Fact]
    public async Task A_password_saved_elsewhere_is_reported_as_the_password_that_needs_entering_again()
    {
        var (storage, here) = await OpenAsync();
        using var _ = storage;
        var elsewhere = new SqliteSubtitleProviderRepository(
            storage.Factory, new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T09:00:00Z")), new DataProtectionSecretProtector(DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(storage.DataRoot, "keys-elsewhere")))));
        await elsewhere.SaveAsync("podnapisi", "Podnapisi",
            new SaveSubtitleProviderRequest("podnapisi", "user", "pass", null, 1, true), CancellationToken.None);

        var row = Assert.Single(await here.ListAsync(CancellationToken.None));

        Assert.True(row.SecretUnreadable);
        Assert.Contains("password", row.SecretProblem);
    }

    [Fact]
    public void A_connection_says_whether_it_holds_a_password_and_a_key_and_an_option_carries_it()
    {
        var now = DateTimeOffset.Parse("2026-04-29T09:00:00Z");
        var held = new SubtitleProviderConnection("id", "k", "K", "u", "s", "a", 1, true, "ok", null, null, null, 0, null, null, now, now);
        var empty = held with { Secret = " ", ApiKey = null };

        Assert.True(held.HasSecret);
        Assert.True(held.HasApiKey);
        Assert.False(empty.HasSecret);
        Assert.False(empty.HasApiKey);

        var option = new SubtitleProviderOption("k", "K", "About K", "Both", true, true, false, true, held);
        Assert.Same(held, option.Configured);
        var result = new SubtitleProviderTestResult(true, "ok", "Fine.", 12, 3);
        Assert.Null(result.Failure);
    }
}
