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
/// The refusals and rules around signing in, signing out, changing the password and API keys (platform AUT-01, 03, 07, 08, 09, 10),
/// proved over HTTP on the real application with a real database file.
/// </summary>
public sealed class SigningInAndKeysOnTheRealHostTests
{
    private const string OwnerPassword = "Deluno-Test-2026!";

    private static async Task<SqliteConnection> OpenAsync(ApplicationTestHost app)
    {
        var factory = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        return (SqliteConnection)await factory.OpenConnectionAsync(DelunoDatabaseNames.Platform, CancellationToken.None);
    }

    private static async Task<long> CountAsync(ApplicationTestHost app, string sql)
    {
        await using var connection = await OpenAsync(app);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(CancellationToken.None));
    }

    private static async Task<string?> TextAsync(ApplicationTestHost app, string sql)
    {
        await using var connection = await OpenAsync(app);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(CancellationToken.None);
        return value is null or DBNull ? null : value.ToString();
    }

    private static async Task<string> LoginAsync(ApplicationTestHost app, string password = OwnerPassword)
    {
        var response = await app.CreateAnonymousClient().PostAsJsonAsync("/api/auth/login", new LoginRequest("owner", password));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;
    }

    private static HttpClient Bearer(ApplicationTestHost app, string token)
    {
        var client = app.CreateAnonymousClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task The_first_account_is_refused_with_a_short_password_a_blank_name_or_a_blank_username_and_none_is_created()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await using (var connection = await OpenAsync(app))
        {
            await using var wipe = connection.CreateCommand();
            wipe.CommandText = "DELETE FROM api_keys; DELETE FROM users;";
            await wipe.ExecuteNonQueryAsync();
        }
        var anonymous = app.CreateAnonymousClient();

        var shortPassword = await anonymous.PostAsJsonAsync("/api/auth/bootstrap", new BootstrapUserRequest("first", "First Person", "short7x"));
        var blankName = await anonymous.PostAsJsonAsync("/api/auth/bootstrap", new BootstrapUserRequest("first", "  ", "a-long-password-1"));
        var blankUser = await anonymous.PostAsJsonAsync("/api/auth/bootstrap", new BootstrapUserRequest("  ", "First Person", "a-long-password-1"));

        Assert.Equal(HttpStatusCode.BadRequest, shortPassword.StatusCode);
        Assert.Contains("at least 8 characters", await shortPassword.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, blankName.StatusCode);
        Assert.Contains("name Deluno should show", await blankName.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, blankUser.StatusCode);
        Assert.Contains("username", await blankUser.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(0, await CountAsync(app, "SELECT COUNT(*) FROM users;"));
        Assert.True((await anonymous.GetFromJsonAsync<BootstrapStatusResponse>("/api/auth/bootstrap-status"))!.RequiresSetup);

        // Exactly eight characters is enough, and the account then exists.
        var exactlyEight = await anonymous.PostAsJsonAsync("/api/auth/bootstrap", new BootstrapUserRequest("first", "First Person", "12345678"));
        Assert.Equal(HttpStatusCode.OK, exactlyEight.StatusCode);
        Assert.Equal(1, await CountAsync(app, "SELECT COUNT(*) FROM users;"));
    }

    [Fact]
    public async Task A_wrong_password_and_an_unknown_user_get_the_same_bare_401_and_blank_fields_are_a_400_naming_both_fields()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var anonymous = app.CreateAnonymousClient();

        var wrongPassword = await anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest("owner", "not-the-password"));
        var unknownUser = await anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest("nobody-here", OwnerPassword));
        var blankPassword = await anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest("owner", ""));
        var blankUser = await anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest(" ", OwnerPassword));

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownUser.StatusCode);
        // No hint which part was wrong: the two answers carry the same body.
        var wrongBody = await wrongPassword.Content.ReadAsStringAsync();
        var unknownBody = await unknownUser.Content.ReadAsStringAsync();
        Assert.Equal(wrongBody, unknownBody);
        Assert.DoesNotContain("password", wrongBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("user", wrongBody, StringComparison.OrdinalIgnoreCase);

        foreach (var blank in new[] { blankPassword, blankUser })
        {
            Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
            var text = await blank.Content.ReadAsStringAsync();
            Assert.Contains("Username is required.", text, StringComparison.Ordinal);
            Assert.Contains("Password is required.", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Signing_out_ends_every_token_the_person_holds_not_only_the_one_that_signed_out()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var laptop = Bearer(app, await LoginAsync(app));
        var phone = Bearer(app, await LoginAsync(app));
        Assert.Equal(HttpStatusCode.OK, (await laptop.GetAsync("/api/tags")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await phone.GetAsync("/api/tags")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await laptop.PostAsync("/api/auth/logout", content: null)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await laptop.GetAsync("/api/tags")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetAsync("/api/tags")).StatusCode);
        // The account itself is untouched: signing in again works.
        Assert.Equal(HttpStatusCode.OK, (await Bearer(app, await LoginAsync(app)).GetAsync("/api/tags")).StatusCode);
    }

    [Fact]
    public async Task Changing_the_password_needs_the_current_one_eight_characters_and_a_different_one_and_ends_the_old_tokens()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var other = Bearer(app, await LoginAsync(app));

        var blankCurrent = await app.Client.PutAsJsonAsync("/api/auth/password", new ChangePasswordRequest("", "A-brand-new-pass-9"));
        var shortNew = await app.Client.PutAsJsonAsync("/api/auth/password", new ChangePasswordRequest(OwnerPassword, "short7x"));
        var same = await app.Client.PutAsJsonAsync("/api/auth/password", new ChangePasswordRequest(OwnerPassword, OwnerPassword));
        var wrongCurrent = await app.Client.PutAsJsonAsync("/api/auth/password", new ChangePasswordRequest("wrong-current", "A-brand-new-pass-9"));

        Assert.Equal(HttpStatusCode.BadRequest, blankCurrent.StatusCode);
        Assert.Contains("Enter your current password.", await blankCurrent.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, shortNew.StatusCode);
        Assert.Contains("at least 8 characters", await shortNew.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, same.StatusCode);
        Assert.Contains("different from your current password", await same.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, wrongCurrent.StatusCode);
        // Every refusal changed nothing: no token was ended.
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/api/tags")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent,
            (await app.Client.PutAsJsonAsync("/api/auth/password", new ChangePasswordRequest(OwnerPassword, "A-brand-new-pass-9"))).StatusCode);

        // Old tokens die: the one that changed it and the one on another device.
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Client.GetAsync("/api/tags")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.GetAsync("/api/tags")).StatusCode);
    }

    [Fact]
    public async Task A_key_asked_for_no_scope_an_unknown_scope_or_a_template_mixed_with_scopes_is_refused_and_nothing_is_stored()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var before = await CountAsync(app, "SELECT COUNT(*) FROM api_keys;");

        var none = await app.Client.PostAsJsonAsync("/api/api-keys", new CreateApiKeyRequest("No scope", ""));
        var unknown = await app.Client.PostAsJsonAsync("/api/api-keys", new CreateApiKeyRequest("Odd scope", "read, launch-missiles"));
        var mixed = await app.Client.PostAsJsonAsync("/api/api-keys", new CreateApiKeyRequest("Mixed", "dashboard-read, write"));

        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Contains("Say what this key may do", await none.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Contains("launch-missiles", await unknown.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, mixed.StatusCode);
        Assert.Contains("on its own", await mixed.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(before, await CountAsync(app, "SELECT COUNT(*) FROM api_keys;"));
    }

    [Fact]
    public async Task A_key_is_stored_as_a_hash_with_the_scopes_it_was_given_and_the_secret_is_shown_only_when_it_is_created()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var create = await app.Client.PostAsJsonAsync("/api/api-keys", new CreateApiKeyRequest("Scoped", "read, queue"));
        var created = await create.Content.ReadFromJsonAsync<CreatedApiKeyResponse>();

        Assert.Equal("read, queue", created!.Item.Scopes);
        var storedHash = await TextAsync(app, "SELECT key_hash FROM api_keys;");
        Assert.False(string.IsNullOrWhiteSpace(storedHash));
        Assert.NotEqual(created.ApiKey, storedHash);
        var everythingStored = await TextAsync(app, "SELECT group_concat(id || name || prefix || scopes || key_hash) FROM api_keys;");
        Assert.DoesNotContain(created.ApiKey, everythingStored ?? "", StringComparison.Ordinal);
        var list = await app.Client.GetStringAsync("/api/api-keys");
        Assert.DoesNotContain(created.ApiKey, list, StringComparison.Ordinal);
        Assert.DoesNotContain(storedHash!, list, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Using_a_key_stamps_when_it_was_last_used_but_writes_that_stamp_at_most_once_a_minute()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var created = await (await app.Client.PostAsJsonAsync("/api/api-keys", new CreateApiKeyRequest("Busy", "read"))).Content.ReadFromJsonAsync<CreatedApiKeyResponse>();
        Assert.Null(await TextAsync(app, "SELECT last_used_utc FROM api_keys;"));
        var key = app.CreateAnonymousClient();
        key.DefaultRequestHeaders.Add("X-Api-Key", created!.ApiKey);

        Assert.Equal(HttpStatusCode.OK, (await key.GetAsync("/api/tags")).StatusCode);
        var first = await TextAsync(app, "SELECT last_used_utc FROM api_keys;");
        Assert.False(string.IsNullOrWhiteSpace(first));

        // Several more calls inside the same minute do not write again.
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await key.GetAsync("/api/tags")).StatusCode);
        }

        Assert.Equal(first, await TextAsync(app, "SELECT last_used_utc FROM api_keys;"));
    }

    [Theory]
    [InlineData("read", "GET", "/api/tags", HttpStatusCode.OK)]
    [InlineData("read", "POST", "/api/tags", HttpStatusCode.Forbidden)]
    [InlineData("write", "POST", "/api/backups", HttpStatusCode.Forbidden)]
    [InlineData("queue", "POST", "/api/tags", HttpStatusCode.Forbidden)]
    [InlineData("imports", "POST", "/api/backups", HttpStatusCode.Forbidden)]
    [InlineData("system", "POST", "/api/tags", HttpStatusCode.Forbidden)]
    public async Task A_key_is_let_in_only_by_the_scope_a_route_names_and_refused_with_403_otherwise(string scopes, string method, string path, HttpStatusCode expected)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var created = await (await app.Client.PostAsJsonAsync("/api/api-keys", new CreateApiKeyRequest("Scope " + scopes, scopes))).Content.ReadFromJsonAsync<CreatedApiKeyResponse>();
        var key = app.CreateAnonymousClient();
        key.DefaultRequestHeaders.Add("X-Api-Key", created!.ApiKey);
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
        {
            request.Content = JsonContent.Create(new { name = "scope-probe" });
        }

        var response = await key.SendAsync(request);

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task The_all_scope_opens_every_area_and_has_to_be_asked_for_by_name()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var created = await (await app.Client.PostAsJsonAsync("/api/api-keys", new CreateApiKeyRequest("Everything", "all"))).Content.ReadFromJsonAsync<CreatedApiKeyResponse>();
        var key = app.CreateAnonymousClient();
        key.DefaultRequestHeaders.Add("X-Api-Key", created!.ApiKey);

        Assert.Equal("all", created.Item.Scopes);
        Assert.Equal(HttpStatusCode.OK, (await key.GetAsync("/api/backups")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await key.GetAsync("/api/tags")).StatusCode);
    }
}
