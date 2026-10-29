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
/// The sign-in and API-key routes on the real host (coverage register #1329, area shell): the answers a caller sees for a
/// blank sign-in, how long a session lasts, which header carries a key, and what a refused key is told.
/// </summary>
public sealed class AccountAndKeyRoutesRowsTests
{
    private const string OwnerPassword = "Deluno-Test-2026!";

    private static async Task<(string Key, string Id)> MakeKeyAsync(ApplicationTestHost app, string scopes)
    {
        var made = await app.Client.PostAsJsonAsync("/api/api-keys", new { name = $"Key {scopes}", scopes });
        Assert.Equal(HttpStatusCode.OK, made.StatusCode);
        var body = await made.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("apiKey").GetString()!, body.GetProperty("item").GetProperty("id").GetString()!);
    }

    private static HttpClient WithHeader(ApplicationTestHost app, string name, string value)
    {
        var client = app.CreateAnonymousClient();
        client.DefaultRequestHeaders.Add(name, value);
        return client;
    }

    [Fact]
    public async Task The_bootstrap_status_is_public_and_says_setup_is_needed_only_while_there_is_no_user()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var anonymous = app.CreateAnonymousClient();

        var configured = await anonymous.GetAsync("/api/auth/bootstrap-status");
        Assert.Equal(HttpStatusCode.OK, configured.StatusCode);
        Assert.False((await configured.Content.ReadFromJsonAsync<BootstrapStatusResponse>())!.RequiresSetup);

        var factory = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        await using (var connection = (SqliteConnection)await factory.OpenConnectionAsync(DelunoDatabaseNames.Platform, CancellationToken.None))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM api_keys; DELETE FROM users;";
            await command.ExecuteNonQueryAsync();
        }

        var fresh = await anonymous.GetAsync("/api/auth/bootstrap-status");
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        Assert.True((await fresh.Content.ReadFromJsonAsync<BootstrapStatusResponse>())!.RequiresSetup);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("owner", "")]
    [InlineData("", "a-password")]
    [InlineData("   ", "a-password")]
    [InlineData("owner", "   ")]
    public async Task A_blank_username_or_password_is_a_validation_problem_that_names_what_is_required(string username, string password)
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.CreateAnonymousClient().PostAsJsonAsync("/api/auth/login", new LoginRequest(username, password));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var errors = document.RootElement.GetProperty("errors");
        Assert.Equal("Username is required.", errors.GetProperty("username")[0].GetString());
        Assert.Equal("Password is required.", errors.GetProperty("password")[0].GetString());
    }

    [Fact]
    public async Task Keeping_the_person_signed_in_makes_the_session_thirty_days_instead_of_seven()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var anonymous = app.CreateAnonymousClient();

        var ordinary = await (await anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest("owner", OwnerPassword))).Content.ReadFromJsonAsync<LoginResponse>();
        var kept = await (await anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest("owner", OwnerPassword, KeepSignedIn: true))).Content.ReadFromJsonAsync<LoginResponse>();

        var now = DateTimeOffset.UtcNow;
        Assert.InRange((ordinary!.ExpiresUtc - now).TotalDays, 6.9, 7.1);
        Assert.InRange((kept!.ExpiresUtc - now).TotalDays, 29.9, 30.1);
    }

    [Fact]
    public async Task A_key_is_accepted_in_X_Api_Key_or_as_a_Bearer_token_and_a_wrong_key_is_401_in_both()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (key, _) = await MakeKeyAsync(app, "read");

        Assert.StartsWith("deluno_", key, StringComparison.Ordinal);
        using var header = WithHeader(app, "X-Api-Key", key);
        using var bearer = app.CreateAnonymousClient();
        bearer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        Assert.Equal(HttpStatusCode.OK, (await header.GetAsync("/api/api-keys/scope-templates")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await bearer.GetAsync("/api/api-keys/scope-templates")).StatusCode);

        using var wrongHeader = WithHeader(app, "X-Api-Key", "deluno_not-a-real-key");
        using var wrongBearer = app.CreateAnonymousClient();
        wrongBearer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "deluno_not-a-real-key");
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrongHeader.GetAsync("/api/api-keys/scope-templates")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrongBearer.GetAsync("/api/api-keys/scope-templates")).StatusCode);
    }

    [Fact]
    public async Task A_key_without_the_scope_a_route_needs_gets_403_and_the_body_names_the_scope_it_lacks_and_the_scopes_it_holds()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (key, _) = await MakeKeyAsync(app, "read");
        using var readOnly = WithHeader(app, "X-Api-Key", key);

        var refused = await readOnly.PostAsJsonAsync("/api/command", new { name = "Backup" });

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        var body = await refused.Content.ReadFromJsonAsync<JsonElement>();
        var required = body.GetProperty("required").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.NotEmpty(required);
        Assert.DoesNotContain("read", required);
        Assert.Equal(new[] { "read" }, body.GetProperty("granted").EnumerateArray().Select(item => item.GetString()!).ToArray());
        var error = body.GetProperty("error").GetString()!;
        Assert.Contains("does not have the", error, StringComparison.Ordinal);
        Assert.Contains(required[0]!, error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_scope_templates_are_listed_to_a_read_only_caller()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (key, _) = await MakeKeyAsync(app, "read");
        using var readOnly = WithHeader(app, "X-Api-Key", key);

        var response = await readOnly.GetAsync("/api/api-keys/scope-templates");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var templates = (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToArray();
        var ids = templates.Select(template => template.GetProperty("id").GetString()).ToArray();
        Assert.Contains("dashboard-read", ids);
        Assert.Contains("media-automation", ids);
        Assert.Contains("full-local", ids);
        Assert.All(templates, template => Assert.NotEmpty(template.GetProperty("scopes").EnumerateArray()));
    }

    [Fact]
    public async Task Creating_a_key_takes_a_name_and_a_scope_or_a_template_and_shows_the_key_once()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var byScope = await app.Client.PostAsJsonAsync("/api/api-keys", new { name = "By scope", scopes = "read,queue" });
        var byTemplate = await app.Client.PostAsJsonAsync("/api/api-keys", new { name = "By template", scopes = "dashboard-read" });
        Assert.Equal(HttpStatusCode.OK, byScope.StatusCode);
        Assert.Equal(HttpStatusCode.OK, byTemplate.StatusCode);
        var scoped = await byScope.Content.ReadFromJsonAsync<JsonElement>();
        var templated = await byTemplate.Content.ReadFromJsonAsync<JsonElement>();

        Assert.StartsWith("deluno_", scoped.GetProperty("apiKey").GetString(), StringComparison.Ordinal);
        Assert.Equal("read, queue", scoped.GetProperty("item").GetProperty("scopes").GetString());
        Assert.Equal("read", templated.GetProperty("item").GetProperty("scopes").GetString());

        var listed = await app.Client.GetStringAsync("/api/api-keys");
        Assert.DoesNotContain(scoped.GetProperty("apiKey").GetString()!, listed, StringComparison.Ordinal);
        Assert.DoesNotContain(templated.GetProperty("apiKey").GetString()!, listed, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_key_with_a_blank_name_is_refused_and_none_is_made(string name)
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsJsonAsync("/api/api-keys", new { name, scopes = "read" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Give this API key a clear name.", document.RootElement.GetProperty("errors").GetProperty("name")[0].GetString());
        using var listed = JsonDocument.Parse(await app.Client.GetStringAsync("/api/api-keys"));
        Assert.Equal(0, listed.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task A_key_that_names_no_scope_or_an_unknown_one_is_refused_instead_of_being_given_every_scope()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var none = await app.Client.PostAsJsonAsync("/api/api-keys", new { name = "No scope", scopes = "" });
        var unknown = await app.Client.PostAsJsonAsync("/api/api-keys", new { name = "Odd scope", scopes = "make-coffee" });

        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        using var listed = JsonDocument.Parse(await app.Client.GetStringAsync("/api/api-keys"));
        Assert.Equal(0, listed.RootElement.GetArrayLength());
    }
}
