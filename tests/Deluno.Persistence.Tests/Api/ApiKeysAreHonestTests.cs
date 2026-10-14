using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Deluno.Infrastructure.Storage;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Integration API keys (the external integration API row group of the register): a key stops working the moment it is revoked,
/// only its hash is stored, and managing keys needs the system scope. Everything goes through the real host and its real
/// platform database.
/// </summary>
public sealed class ApiKeysAreHonestTests
{
    [Fact]
    public async Task A_revoked_key_is_refused_on_the_very_next_request()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (id, key) = await CreateKeyAsync(app, "read");
        using var keyed = KeyClient(app, key);

        using (var before = await keyed.GetAsync("/api/api-keys/scope-templates"))
        {
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        }

        using (var revoke = await app.Client.DeleteAsync($"/api/api-keys/{id}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        }

        using var after = await keyed.GetAsync("/api/api-keys/scope-templates");
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task Only_the_hash_of_a_key_is_stored_and_the_key_is_in_no_list_or_row()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (id, key) = await CreateKeyAsync(app, "read");

        await using var connection = await app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>()
            .OpenConnectionAsync(DelunoDatabaseNames.Platform, CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM api_keys WHERE id = @id";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@id";
        parameter.Value = id;
        command.Parameters.Add(parameter);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        var expectedHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(key.Trim())));
        string? storedHash = null;
        for (var column = 0; column < reader.FieldCount; column++)
        {
            var value = reader.IsDBNull(column) ? null : Convert.ToString(reader.GetValue(column));
            Assert.False(value is not null && value.Contains(key, StringComparison.Ordinal), $"column {reader.GetName(column)} holds the raw key");
            if (reader.GetName(column) == "key_hash")
            {
                storedHash = value;
            }
        }

        Assert.NotNull(storedHash);
        Assert.NotEqual(key, storedHash);
        Assert.Equal(expectedHash, storedHash);

        var list = await app.Client.GetStringAsync("/api/api-keys");
        Assert.DoesNotContain(key, list, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("write")]
    [InlineData("queue")]
    public async Task A_key_without_the_system_scope_cannot_create_or_revoke_keys(string scopes)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (otherId, otherKey) = await CreateKeyAsync(app, "read");
        var (_, key) = await CreateKeyAsync(app, scopes);
        using var keyed = KeyClient(app, key);

        using var create = await keyed.PostAsJsonAsync("/api/api-keys", new { name = "Sneaky", scopes = "read" });
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Contains("system", await create.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        using var revoke = await keyed.DeleteAsync($"/api/api-keys/{otherId}");
        Assert.Equal(HttpStatusCode.Forbidden, revoke.StatusCode);

        // The key that was not allowed to be revoked still works.
        using var other = KeyClient(app, otherKey);
        using var still = await other.GetAsync("/api/api-keys/scope-templates");
        Assert.Equal(HttpStatusCode.OK, still.StatusCode);
    }

    [Fact]
    public async Task A_key_with_the_system_scope_creates_and_revokes_keys()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (_, systemKey) = await CreateKeyAsync(app, "system");
        using var keyed = KeyClient(app, systemKey);

        using var create = await keyed.PostAsJsonAsync("/api/api-keys", new { name = "Made by a system key", scopes = "read" });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var made = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("item").GetProperty("id").GetString();

        using var revoke = await keyed.DeleteAsync($"/api/api-keys/{made}");
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
    }

    [Fact(Skip = "DEFECT: GET /api/api-keys answers 200 to a read-only key (key names, scopes and prefixes), although the route group is declared system-scoped and the code comment says list operations stay system-scoped.")]
    public async Task A_read_only_key_cannot_list_the_keys()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (_, key) = await CreateKeyAsync(app, "read");
        using var keyed = KeyClient(app, key);

        using var list = await keyed.GetAsync("/api/api-keys");

        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
    }

    private static async Task<(string Id, string Key)> CreateKeyAsync(ApplicationTestHost app, string scopes)
    {
        using var created = await app.Client.PostAsJsonAsync("/api/api-keys", new { name = "Key " + scopes, scopes });
        created.EnsureSuccessStatusCode();
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("item").GetProperty("id").GetString()!, body.GetProperty("apiKey").GetString()!);
    }

    private static HttpClient KeyClient(ApplicationTestHost app, string key)
    {
        var client = app.CreateAnonymousClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);
        return client;
    }
}
