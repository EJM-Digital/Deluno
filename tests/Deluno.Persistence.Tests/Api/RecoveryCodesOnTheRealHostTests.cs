using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Infrastructure.Storage;
using Deluno.Jobs.Data;
using Deluno.Notifications;
using Deluno.Persistence.Tests.Support;
using Deluno.Security.Contracts;
using Deluno.Security.Recovery;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1392: recovery codes, over HTTP on the real application with a real database file.
/// </summary>
public sealed class RecoveryCodesOnTheRealHostTests
{
    private const string OwnerPassword = "Deluno-Test-2026!";
    private const string NewPassword = "a-brand-new-password";
    private const string Refused = "That did not work. Check it and try again.";

    private sealed class RecordingNotifications : IOutboundNotificationService
    {
        public List<(string Category, string Title, string Message)> Sent { get; } = [];

        public Task DispatchAsync(string eventCategory, string title, string message, string? detailsJson, CancellationToken cancellationToken, IReadOnlyList<string>? titleTags = null)
        {
            Sent.Add((eventCategory, title, message));
            return Task.CompletedTask;
        }

        public Task<Deluno.Notifications.Contracts.NotificationWebhookDeliveryResult?> DispatchToWebhookAsync(string webhookId, string eventCategory, string title, string message, string? detailsJson, CancellationToken cancellationToken)
            => Task.FromResult<Deluno.Notifications.Contracts.NotificationWebhookDeliveryResult?>(null);

        public Task<Deluno.Notifications.Contracts.NotificationWebhookDeliveryResult?> ReplayAsync(string deliveryId, CancellationToken cancellationToken)
            => Task.FromResult<Deluno.Notifications.Contracts.NotificationWebhookDeliveryResult?>(null);
    }

    private static Task<ApplicationTestHost> StartAsync(
        FakeTimeProvider? clock = null,
        RecordingNotifications? notifications = null)
        => ApplicationTestHost.StartAsync(replaceServices: services =>
        {
            if (clock is not null)
            {
                services.AddSingleton<TimeProvider>(clock);
            }

            if (notifications is not null)
            {
                services.AddSingleton<IOutboundNotificationService>(notifications);
            }

        });

    private static async Task<IReadOnlyList<string>> MakeCodesAsync(ApplicationTestHost app)
    {
        var made = await app.Client.PostAsync("/api/auth/recovery-codes", null);
        Assert.Equal(HttpStatusCode.OK, made.StatusCode);
        return (await made.Content.ReadFromJsonAsync<RecoveryCodesMade>())!.Codes;
    }

    private static async Task<HttpResponseMessage> CheckAsync(ApplicationTestHost app, string? code = null)
        => await app.CreateAnonymousClient().PostAsJsonAsync("/api/auth/recovery/check", new RecoveryProofRequest(code));

    private static async Task<HttpResponseMessage> ResetAsync(ApplicationTestHost app, string? code = null, string password = NewPassword)
        => await app.CreateAnonymousClient().PostAsJsonAsync("/api/auth/recovery/reset", new RecoveryResetRequest(code, password));

    private static async Task<string> MessageAsync(HttpResponseMessage response)
        => (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString()!;

    private static async Task<HttpStatusCode> SignInAsync(ApplicationTestHost app, string password)
        => (await app.CreateAnonymousClient().PostAsJsonAsync("/api/auth/login", new LoginRequest("owner", password))).StatusCode;

    private static async Task<long> CountAsync(ApplicationTestHost app, string sql)
    {
        var factory = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        await using var connection = (SqliteConnection)await factory.OpenConnectionAsync(DelunoDatabaseNames.Platform, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(CancellationToken.None));
    }

    private static async Task<RecoveryCodesStatus> ReadStatusAsync(ApplicationTestHost app, string password)
    {
        var client = app.CreateAnonymousClient();
        var login = await (await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("owner", password))).Content.ReadFromJsonAsync<LoginResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);
        return (await client.GetFromJsonAsync<RecoveryCodesStatus>("/api/auth/recovery-codes"))!;
    }

    [Fact]
    public async Task Creating_the_first_account_makes_ten_codes_and_stores_only_their_hashes()
    {
        await using var app = await StartAsync();
        var factory = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        await using (var connection = (SqliteConnection)await factory.OpenConnectionAsync(DelunoDatabaseNames.Platform, CancellationToken.None))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM api_keys; DELETE FROM users;";
            await command.ExecuteNonQueryAsync();
        }

        var response = await app.CreateAnonymousClient().PostAsJsonAsync(
            "/api/auth/bootstrap",
            new BootstrapUserRequest("founder", "Founder", OwnerPassword));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<BootstrapResponse>())!;
        Assert.Equal(10, body.RecoveryCodes.Count);
        Assert.Equal(10, body.RecoveryCodes.Distinct().Count());
        Assert.All(body.RecoveryCodes, code => Assert.Matches("^[A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4}$", code));
        Assert.Equal(10, await CountAsync(app, "SELECT COUNT(*) FROM recovery_codes WHERE used_utc IS NULL"));

        var stored = new List<string>();
        await using (var connection = (SqliteConnection)await factory.OpenConnectionAsync(DelunoDatabaseNames.Platform, CancellationToken.None))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT code_hash FROM recovery_codes";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                stored.Add(reader.GetString(0));
            }
        }

        Assert.Equal(10, stored.Count);
        Assert.All(stored, hash => Assert.StartsWith("100000.", hash));
        Assert.DoesNotContain(stored, hash => body.RecoveryCodes.Any(code => hash.Contains(code.Replace("-", "", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task An_account_made_before_codes_existed_shows_none_made_and_a_new_set_voids_the_old_one()
    {
        await using var app = await StartAsync();

        var before = (await (await app.Client.GetAsync("/api/auth/recovery-codes")).Content.ReadFromJsonAsync<RecoveryCodesStatus>())!;
        Assert.Equal(new RecoveryCodesStatus(0, false), before);

        var first = await MakeCodesAsync(app);
        Assert.Equal(10, first.Count);
        var afterFirst = (await (await app.Client.GetAsync("/api/auth/recovery-codes")).Content.ReadFromJsonAsync<RecoveryCodesStatus>())!;
        Assert.Equal(new RecoveryCodesStatus(10, true), afterFirst);

        var second = await MakeCodesAsync(app);
        Assert.Empty(first.Intersect(second));
        Assert.Equal(10, await CountAsync(app, "SELECT COUNT(*) FROM recovery_codes"));
        Assert.Equal(HttpStatusCode.BadRequest, (await CheckAsync(app, first[0])).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await CheckAsync(app, second[0])).StatusCode);
    }

    [Fact]
    public async Task The_codes_screens_need_a_signed_in_person()
    {
        await using var app = await StartAsync();
        var anonymous = app.CreateAnonymousClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/auth/recovery-codes")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/auth/recovery-codes", null)).StatusCode);
    }

    [Fact]
    public async Task A_valid_code_shows_the_username_and_sets_a_new_password_and_the_code_is_then_used_up()
    {
        await using var app = await StartAsync();
        var codes = await MakeCodesAsync(app);

        // Typed in lower case, with spaces instead of dashes: still the same code.
        var typed = codes[3].ToLowerInvariant().Replace('-', ' ');
        var check = await CheckAsync(app, typed);
        Assert.Equal(HttpStatusCode.OK, check.StatusCode);
        Assert.Equal("owner", (await check.Content.ReadFromJsonAsync<RecoveryUsername>())!.Username);
        // Looking is not using.
        Assert.Equal(10, (await (await app.Client.GetAsync("/api/auth/recovery-codes")).Content.ReadFromJsonAsync<RecoveryCodesStatus>())!.Remaining);

        var reset = await ResetAsync(app, codes[3]);
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);

        Assert.Equal(HttpStatusCode.OK, await SignInAsync(app, NewPassword));
        Assert.Equal(HttpStatusCode.Unauthorized, await SignInAsync(app, OwnerPassword));

        var again = await ResetAsync(app, codes[3], password: "another-new-password");
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Equal(Refused, await MessageAsync(again));
        Assert.Equal(HttpStatusCode.BadRequest, (await CheckAsync(app, codes[3])).StatusCode);
        Assert.Equal(9, (await ReadStatusAsync(app, NewPassword)).Remaining);

        // The other nine still work.
        Assert.Equal(HttpStatusCode.OK, (await CheckAsync(app, codes[4])).StatusCode);
    }

    [Fact]
    public async Task A_new_password_under_eight_characters_is_refused_and_the_code_is_not_used_up()
    {
        await using var app = await StartAsync();
        var codes = await MakeCodesAsync(app);

        var reset = await ResetAsync(app, codes[0], password: "short");

        Assert.Equal(HttpStatusCode.BadRequest, reset.StatusCode);
        Assert.Contains("at least 8 characters", await reset.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, await SignInAsync(app, OwnerPassword));
        Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync(app, codes[0])).StatusCode);
    }

    [Fact]
    public async Task A_wrong_code_and_a_blank_one_get_the_same_plain_refusal()
    {
        await using var app = await StartAsync();
        await MakeCodesAsync(app);

        var wrongCode = await CheckAsync(app, "AAAA-BBBB-CCCC");
        var blank = await CheckAsync(app);
        var resetWrong = await ResetAsync(app, "AAAA-BBBB-CCCC");

        foreach (var response in new[] { wrongCode, blank, resetWrong })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(Refused, await MessageAsync(response));
        }

        Assert.Equal(HttpStatusCode.OK, await SignInAsync(app, OwnerPassword));
    }

    [Fact]
    public async Task A_reset_signs_every_session_out_and_keeps_api_keys_working()
    {
        await using var app = await StartAsync();
        var key = await app.Client.PostAsJsonAsync("/api/api-keys", new { name = "Reader", scopes = "read" });
        var rawKey = (await key.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("apiKey").GetString()!;
        var codes = await MakeCodesAsync(app);

        // The owner's session works, then the reset ends it at once (the cached copy of the user is dropped too).
        Assert.Equal(HttpStatusCode.OK, (await app.Client.GetAsync("/api/auth/recovery-codes")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync(app, codes[0])).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Client.GetAsync("/api/auth/recovery-codes")).StatusCode);

        var keyed = app.CreateAnonymousClient();
        keyed.DefaultRequestHeaders.Add("X-Api-Key", rawKey);
        Assert.Equal(HttpStatusCode.OK, (await keyed.GetAsync("/api/api-keys/scope-templates")).StatusCode);
    }

    [Fact]
    public async Task A_reset_by_code_writes_an_activity_line_and_sends_a_notification()
    {
        var notifications = new RecordingNotifications();
        await using var app = await StartAsync(notifications: notifications);
        var codes = await MakeCodesAsync(app);

        await ResetAsync(app, codes[0]);

        var activity = await app.Services.GetRequiredService<IActivityFeedRepository>().ListActivityAsync(20, null, null, CancellationToken.None);
        var line = Assert.Single(activity, item => item.Category == "security.password-reset");
        Assert.Equal("Password reset with a recovery code. Every signed-in session was ended.", line.Message);
        var sent = Assert.Single(notifications.Sent);
        Assert.Equal("security.password-reset", sent.Category);
        Assert.Equal("Deluno password reset", sent.Title);
        Assert.Equal(line.Message, sent.Message);
    }

    [Fact]
    public async Task A_refused_try_writes_no_activity_and_sends_nothing()
    {
        var notifications = new RecordingNotifications();
        await using var app = await StartAsync(notifications: notifications);

        await ResetAsync(app, "AAAA-BBBB-CCCC");

        var activity = await app.Services.GetRequiredService<IActivityFeedRepository>().ListActivityAsync(20, null, null, CancellationToken.None);
        Assert.DoesNotContain(activity, item => item.Category == "security.password-reset");
        Assert.Empty(notifications.Sent);
    }

    [Fact]
    public async Task Five_wrong_tries_lock_recovery_for_ten_minutes_even_for_a_right_code()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var app = await StartAsync(clock);
        var codes = await MakeCodesAsync(app);

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await CheckAsync(app, "AAAA-BBBB-CCCC")).StatusCode);
        }

        var locked = await CheckAsync(app, codes[0]);
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.Equal("Too many tries. Wait 10 minutes and try again.", await MessageAsync(locked));
        Assert.Equal(HttpStatusCode.TooManyRequests, (await ResetAsync(app, codes[0])).StatusCode);

        clock.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.OK, (await CheckAsync(app, codes[0])).StatusCode);
    }

    [Fact]
    public async Task A_right_try_in_between_clears_the_count_of_wrong_ones()
    {
        await using var app = await StartAsync();
        var codes = await MakeCodesAsync(app);

        for (var round = 0; round < 3; round++)
        {
            for (var i = 0; i < 4; i++)
            {
                await CheckAsync(app, "AAAA-BBBB-CCCC");
            }

            Assert.Equal(HttpStatusCode.OK, (await CheckAsync(app, codes[0])).StatusCode);
        }
    }

    [Fact]
    public void Typed_codes_are_compared_without_spaces_dashes_or_case()
    {
        Assert.Equal("ABCD2345WXYZ", RecoveryCodeFormat.Normalize(" abcd-2345 wxyz "));
        Assert.Equal(string.Empty, RecoveryCodeFormat.Normalize(null));
    }
}
