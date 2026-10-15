using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Infrastructure.Storage;
using Deluno.Persistence.Tests.Support;
using Deluno.Security.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The account routes on the real host and its real SQLite file (coverage register, area shell): every refusal says
/// what was wrong in the words the screen shows, and every success changes exactly what it says.
/// </summary>
public sealed class AccountRoutesEveryAnswerTests
{
    private const string OwnerPassword = "Deluno-Test-2026!";

    private static async Task<long> UserCountAsync(ApplicationTestHost app)
    {
        var factory = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        await using var connection = (SqliteConnection)await factory.OpenConnectionAsync(DelunoDatabaseNames.Platform, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM users;";
        return Convert.ToInt64(await command.ExecuteScalarAsync(CancellationToken.None));
    }

    private static async Task EmptyTheAccountsAsync(ApplicationTestHost app)
    {
        var factory = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        await using var connection = await factory.OpenConnectionAsync(DelunoDatabaseNames.Platform, CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM api_keys; DELETE FROM users;";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ProblemForAsync(HttpResponseMessage response, string field)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("errors").GetProperty(field)[0].GetString()!;
    }

    private static async Task<HttpClient> SignedInAsync(ApplicationTestHost app, string password = OwnerPassword)
    {
        var client = app.CreateAnonymousClient();
        var login = await (await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("owner", password)))
            .Content.ReadFromJsonAsync<LoginResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);
        return client;
    }

    [Fact]
    public async Task Bootstrap_names_each_field_that_is_missing_or_too_short()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await EmptyTheAccountsAsync(app);
        var anonymous = app.CreateAnonymousClient();

        var nothing = await anonymous.PostAsJsonAsync("/api/auth/bootstrap", new BootstrapUserRequest("", "", ""));
        Assert.Equal("Give this user a username.", await ProblemForAsync(nothing, "username"));
        var noPassword = await anonymous.PostAsJsonAsync("/api/auth/bootstrap", new BootstrapUserRequest("first", "First", ""));
        Assert.Equal("Give this user a password.", await ProblemForAsync(noPassword, "password"));
        var shortPassword = await anonymous.PostAsJsonAsync("/api/auth/bootstrap", new BootstrapUserRequest("first", "First", "1234567"));
        Assert.Equal("Use at least 8 characters for the password.", await ProblemForAsync(shortPassword, "password"));
        var noName = await anonymous.PostAsJsonAsync("/api/auth/bootstrap", new BootstrapUserRequest("first", " ", "long-enough-1"));
        Assert.Equal("Choose the name Deluno should show in the app.", await ProblemForAsync(noName, "displayName"));

        Assert.Equal(0, await UserCountAsync(app));
        Assert.True((await anonymous.GetFromJsonAsync<BootstrapStatusResponse>("/api/auth/bootstrap-status"))!.RequiresSetup);
    }

    [Fact]
    public async Task Bootstrap_on_a_configured_install_answers_conflict_and_adds_no_second_owner()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        Assert.Equal(1, await UserCountAsync(app));

        var attempt = await app.CreateAnonymousClient()
            .PostAsJsonAsync("/api/auth/bootstrap", new BootstrapUserRequest("intruder", "Intruder", "a-long-password-1"));

        Assert.Equal(HttpStatusCode.Conflict, attempt.StatusCode);
        Assert.Contains("Deluno has already been configured.", await attempt.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(1, await UserCountAsync(app));
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await app.CreateAnonymousClient().PostAsJsonAsync("/api/auth/login", new LoginRequest("intruder", "a-long-password-1"))).StatusCode);
    }

    [Fact]
    public async Task A_wrong_password_and_an_unknown_user_get_the_same_refusal_after_the_same_work()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var anonymous = app.CreateAnonymousClient();

        var wrong = await anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest("owner", "not-the-password"));
        var unknown = await anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest("nobody", "not-the-password"));
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(await wrong.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());

        // The dummy hash: a name nobody has still pays for a password check, so the time does not say which was wrong.
        async Task<double> FastestAsync(string user)
        {
            var best = double.MaxValue;
            for (var i = 0; i < 3; i++)
            {
                var clock = Stopwatch.StartNew();
                await anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest(user, "not-the-password"));
                best = Math.Min(best, clock.Elapsed.TotalMilliseconds);
            }
            return best;
        }

        var wrongTime = await FastestAsync("owner");
        var unknownTime = await FastestAsync("nobody");
        Assert.True(unknownTime >= wrongTime * 0.25, $"unknown user answered in {unknownTime} ms against {wrongTime} ms for a wrong password");
    }

    [Fact]
    public async Task Login_is_limited_to_ten_attempts_a_minute_then_answers_429_with_retry_after_and_other_routes_are_not_slowed()
    {
        await using var app = await ApplicationTestHost.StartAsync(withRateLimiting: true);
        var anonymous = app.CreateAnonymousClient();

        // StartAsync signed in once through the same route, so nine more attempts fit in the window of ten.
        for (var attempt = 0; attempt < 9; attempt++)
        {
            var refused = await anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest("owner", "not-the-password"));
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }

        var tooMany = await anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest("owner", OwnerPassword));
        Assert.Equal(HttpStatusCode.TooManyRequests, tooMany.StatusCode);
        Assert.True(tooMany.Headers.TryGetValues("Retry-After", out var retryAfter));
        Assert.True(int.Parse(Assert.Single(retryAfter!)) > 0);
        Assert.Equal("application/json", tooMany.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Rate limit exceeded", await tooMany.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // Other routes keep answering: the owner's own token still opens the API, and the status poll is not counted.
        Assert.Equal(HttpStatusCode.OK, (await app.Client.GetAsync("/api/tags")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/api/auth/bootstrap-status")).StatusCode);
    }

    [Fact]
    public async Task Logout_needs_a_session_and_ends_every_session_of_the_user()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var phone = await SignedInAsync(app);
        var laptop = await SignedInAsync(app);
        Assert.Equal(HttpStatusCode.OK, (await phone.GetAsync("/api/tags")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await laptop.GetAsync("/api/tags")).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateAnonymousClient().PostAsync("/api/auth/logout", content: null)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await phone.PostAsync("/api/auth/logout", content: null)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetAsync("/api/tags")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await laptop.GetAsync("/api/tags")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.CreateAnonymousClient().PostAsJsonAsync("/api/auth/login", new LoginRequest("owner", OwnerPassword))).StatusCode);
    }

    [Fact]
    public async Task Changing_the_password_says_what_is_missing_or_too_short_or_unchanged()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var noCurrent = await app.Client.PutAsJsonAsync("/api/auth/password", new ChangePasswordRequest("", "A-brand-new-pass-9"));
        Assert.Equal("Enter your current password.", await ProblemForAsync(noCurrent, "currentPassword"));
        var noNew = await app.Client.PutAsJsonAsync("/api/auth/password", new ChangePasswordRequest(OwnerPassword, ""));
        Assert.Equal("Enter a new password.", await ProblemForAsync(noNew, "newPassword"));
        var shortNew = await app.Client.PutAsJsonAsync("/api/auth/password", new ChangePasswordRequest(OwnerPassword, "short"));
        Assert.Equal("Use at least 8 characters for the new password.", await ProblemForAsync(shortNew, "newPassword"));
        var same = await app.Client.PutAsJsonAsync("/api/auth/password", new ChangePasswordRequest(OwnerPassword, OwnerPassword));
        Assert.Equal("Choose a password that is different from your current password.", await ProblemForAsync(same, "newPassword"));

        Assert.Equal(HttpStatusCode.OK, (await app.CreateAnonymousClient().PostAsJsonAsync("/api/auth/login", new LoginRequest("owner", OwnerPassword))).StatusCode);
    }

    [Fact]
    public async Task A_wrong_current_password_is_named_and_leaves_every_session_working()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var other = await SignedInAsync(app);

        var response = await app.Client.PutAsJsonAsync("/api/auth/password", new ChangePasswordRequest("wrong-current", "A-brand-new-pass-9"));

        Assert.Equal("Current password is not correct.", await ProblemForAsync(response, "currentPassword"));
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/api/tags")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.Client.GetAsync("/api/tags")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await app.CreateAnonymousClient().PostAsJsonAsync("/api/auth/login", new LoginRequest("owner", "A-brand-new-pass-9"))).StatusCode);
    }

    [Fact]
    public async Task The_right_current_password_changes_it_and_ends_the_other_sessions()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var other = await SignedInAsync(app);
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/api/tags")).StatusCode);

        var changed = await app.Client.PutAsJsonAsync("/api/auth/password", new ChangePasswordRequest(OwnerPassword, "A-brand-new-pass-9"));
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await other.GetAsync("/api/tags")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Client.GetAsync("/api/tags")).StatusCode);
        var anonymous = app.CreateAnonymousClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest("owner", OwnerPassword))).StatusCode);
        var fresh = await SignedInAsync(app, "A-brand-new-pass-9");
        Assert.Equal(HttpStatusCode.OK, (await fresh.GetAsync("/api/tags")).StatusCode);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("queue")]
    [InlineData("write")]
    [InlineData("imports")]
    public async Task A_key_without_the_system_scope_cannot_list_create_or_revoke_keys(string scopes)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var made = await app.Client.PostAsJsonAsync("/api/api-keys", new { name = "Narrow key", scopes });
        var body = await made.Content.ReadFromJsonAsync<JsonElement>();
        var narrow = app.CreateAnonymousClient();
        narrow.DefaultRequestHeaders.Add("X-Api-Key", body.GetProperty("apiKey").GetString()!);
        var narrowId = body.GetProperty("item").GetProperty("id").GetString()!;

        Assert.Equal(HttpStatusCode.Forbidden, (await narrow.GetAsync("/api/api-keys")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await narrow.PostAsJsonAsync("/api/api-keys", new { name = "All powerful", scopes = "system" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await narrow.DeleteAsync($"/api/api-keys/{narrowId}")).StatusCode);

        using var listed = JsonDocument.Parse(await app.Client.GetStringAsync("/api/api-keys"));
        Assert.Single(listed.RootElement.EnumerateArray());
    }

    [Fact]
    public async Task A_system_key_can_manage_keys_and_a_revoked_key_stops_at_once()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var made = await app.Client.PostAsJsonAsync("/api/api-keys", new { name = "Admin box", scopes = "system" });
        var body = await made.Content.ReadFromJsonAsync<JsonElement>();
        var systemKey = app.CreateAnonymousClient();
        systemKey.DefaultRequestHeaders.Add("X-Api-Key", body.GetProperty("apiKey").GetString()!);
        var id = body.GetProperty("item").GetProperty("id").GetString()!;

        Assert.Equal(HttpStatusCode.OK, (await systemKey.GetAsync("/api/api-keys")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await systemKey.DeleteAsync("/api/api-keys/no-such-key")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await systemKey.DeleteAsync($"/api/api-keys/{id}")).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await systemKey.GetAsync("/api/api-keys")).StatusCode);
    }

    /// <summary>A clock a test can move: the host reads it for token lifetimes the way it reads the system clock.</summary>
    private sealed class MovableClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    [Fact]
    public async Task A_session_past_half_its_life_gets_a_fresh_token_on_a_normal_request_and_keeps_working_past_the_original_expiry()
    {
        var clock = new MovableClock(DateTimeOffset.UtcNow);
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => services.AddSingleton<TimeProvider>(clock));
        var anonymous = app.CreateAnonymousClient();
        var login = await (await anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest("owner", OwnerPassword))).Content.ReadFromJsonAsync<LoginResponse>();
        Assert.Equal(clock.GetUtcNow().Add(TimeSpan.FromDays(7)).ToUnixTimeSeconds(), login!.ExpiresUtc.ToUnixTimeSeconds());

        HttpRequestMessage Call(string token) => new(HttpMethod.Get, "/api/tags") { Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) } };

        // Early in its life nothing is handed back.
        clock.Advance(TimeSpan.FromDays(2));
        var early = await anonymous.SendAsync(Call(login.AccessToken));
        Assert.Equal(HttpStatusCode.OK, early.StatusCode);
        Assert.False(early.Headers.Contains("X-Deluno-Access-Token"));

        // Past halfway, ordinary use brings back a fresh token that lasts another full week.
        clock.Advance(TimeSpan.FromDays(2));
        var late = await anonymous.SendAsync(Call(login.AccessToken));
        Assert.Equal(HttpStatusCode.OK, late.StatusCode);
        var renewed = Assert.Single(late.Headers.GetValues("X-Deluno-Access-Token"));
        var renewedExpires = DateTimeOffset.Parse(Assert.Single(late.Headers.GetValues("X-Deluno-Access-Token-Expires")));
        Assert.Equal(clock.GetUtcNow().Add(TimeSpan.FromDays(7)).ToUnixTimeSeconds(), renewedExpires.ToUnixTimeSeconds());

        // Three days later the first token (7 days from login) has run out; the renewed one still opens the API.
        clock.Advance(TimeSpan.FromDays(3));
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(Call(login.AccessToken))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.SendAsync(Call(renewed))).StatusCode);
    }

    [Fact]
    public async Task Listing_API_keys_shows_name_prefix_scopes_and_last_used_and_never_the_key_or_its_hash()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var made = await (await app.Client.PostAsJsonAsync("/api/api-keys", new { name = "Home Assistant box", scopes = "read" })).Content.ReadFromJsonAsync<JsonElement>();
        var secret = made.GetProperty("apiKey").GetString()!;
        var keyClient = app.CreateAnonymousClient();
        keyClient.DefaultRequestHeaders.Add("X-Api-Key", secret);
        Assert.Equal(HttpStatusCode.OK, (await keyClient.GetAsync("/api/tags")).StatusCode);

        var listed = await app.Client.GetStringAsync("/api/api-keys");

        using var document = JsonDocument.Parse(listed);
        var item = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal("Home Assistant box", item.GetProperty("name").GetString());
        Assert.Equal("read", item.GetProperty("scopes").GetString());
        Assert.StartsWith(secret[..8], item.GetProperty("prefix").GetString(), StringComparison.Ordinal);
        Assert.NotEqual(JsonValueKind.Null, item.GetProperty("lastUsedUtc").ValueKind);
        Assert.DoesNotContain(secret, listed, StringComparison.Ordinal);
        Assert.DoesNotContain("hash", listed, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(item.EnumerateObject().Where(property => property.Name.Contains("secret", StringComparison.OrdinalIgnoreCase) || property.Name.Contains("key", StringComparison.OrdinalIgnoreCase) && property.Name != "id").ToArray());
    }

    [Fact]
    public async Task Allowed_hosts_overview_offers_the_address_in_use_localhost_and_this_computers_name_each_with_its_reason()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/security/allowed-hosts");
        request.Headers.Host = "deluno.lan:8080";

        var overview = await (await app.Client.SendAsync(request)).Content.ReadFromJsonAsync<AllowedHostsOverview>();

        Assert.Equal("deluno.lan", overview!.CurrentHost);
        Assert.False(overview.Enabled);
        Assert.Equal("The address you're using right now", overview.DetectedHosts.Single(candidate => candidate.Host == "deluno.lan").Reason);
        Assert.Equal("Always answers from this machine", overview.DetectedHosts.Single(candidate => candidate.Host == "localhost").Reason);
        Assert.Equal("Always answers from this machine", overview.DetectedHosts.Single(candidate => candidate.Host == "127.0.0.1").Reason);
        Assert.Equal("This computer's name", overview.DetectedHosts.Single(candidate => candidate.Host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)).Reason);
        Assert.Equal(overview.DetectedHosts.Count, overview.DetectedHosts.Select(candidate => candidate.Host.ToLowerInvariant()).Distinct().Count());
    }

    [Fact]
    public async Task Two_bootstrap_requests_at_once_create_only_one_account()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await EmptyTheAccountsAsync(app);
        var anonymous = app.CreateAnonymousClient();

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(number =>
            anonymous.PostAsJsonAsync("/api/auth/bootstrap", new BootstrapUserRequest($"owner{number}", $"Owner {number}", "a-long-password-1"))));

        Assert.Equal(1, await UserCountAsync(app));
        Assert.Equal(1, results.Count(response => response.StatusCode == HttpStatusCode.OK));
        Assert.All(results.Where(response => response.StatusCode != HttpStatusCode.OK), response => Assert.Equal(HttpStatusCode.Conflict, response.StatusCode));
    }
}
