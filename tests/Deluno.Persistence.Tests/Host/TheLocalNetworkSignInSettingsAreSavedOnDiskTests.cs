using System.Net;
using System.Net.Http.Json;
using Deluno.Persistence.Tests.Support;
using Deluno.Security.Contracts;
using Microsoft.Data.Sqlite;

namespace Deluno.Persistence.Tests.Host;

/// <summary>
/// #1294: "Skip sign-in on the local network" and Allowed Hosts over their real routes on the whole application with its own
/// database file: the owner saves them and what is saved is in the Platform database file itself, a key that only reads is
/// refused and leaves the file as it was, and a bad address saves nothing. (A real device skipping sign-in, and the Allowed
/// Hosts turning away an address not on the list, are in ALanDeviceSkipsSignInOnTheShippedPipelineTests and OtherDevicesAndAllowedHostsOnARealHostTests.)
/// </summary>
public sealed class TheLocalNetworkSignInSettingsAreSavedOnDiskTests
{
    private static long RowsOnDisk(ApplicationTestHost app, string like)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(app.DataRoot, "platform.db")};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM system_settings WHERE setting_key LIKE @like AND setting_value <> ''";
        command.Parameters.AddWithValue("@like", like);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    [Fact]
    public async Task The_owner_saves_the_local_network_setting_and_it_is_in_the_database_file_while_a_read_only_key_is_refused_and_a_bad_address_saves_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var created = await (await app.Client.PostAsJsonAsync("/api/api-keys", new CreateApiKeyRequest("Reader", "read"))).Content.ReadFromJsonAsync<CreatedApiKeyResponse>();
        var reader = app.CreateAnonymousClient();
        reader.DefaultRequestHeaders.Add("X-Api-Key", created!.ApiKey);
        Assert.Equal(0, RowsOnDisk(app, "%local%network%"));

        var refused = await reader.PutAsJsonAsync("/api/security/local-network-auth", new UpdateLocalNetworkAuthRequest(true, ["10.20.0.0/16"], [], []));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(0, RowsOnDisk(app, "%local%network%"));

        var bad = await app.Client.PutAsJsonAsync("/api/security/local-network-auth", new UpdateLocalNetworkAuthRequest(true, ["not-an-address"], [], []));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(0, RowsOnDisk(app, "%local%network%"));

        var saved = await app.Client.PutAsJsonAsync("/api/security/local-network-auth", new UpdateLocalNetworkAuthRequest(true, ["10.20.0.0/16"], ["192.168.1.20"], []));

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.True(RowsOnDisk(app, "%local%network%") > 0, "the saved setting is in the Platform database file");
        var after = await app.Client.GetFromJsonAsync<LocalNetworkAuthOverview>("/api/security/local-network-auth");
        Assert.True(after!.Enabled);
        Assert.Equal(["10.20.0.0/16"], after.AdditionalLocalNetworks);
        Assert.Equal(["192.168.1.20"], after.AllowList);
    }

    [Fact]
    public async Task Allowed_hosts_are_saved_by_the_owner_into_the_database_file_and_a_read_only_key_cannot_change_them()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var created = await (await app.Client.PostAsJsonAsync("/api/api-keys", new CreateApiKeyRequest("Reader", "read"))).Content.ReadFromJsonAsync<CreatedApiKeyResponse>();
        var reader = app.CreateAnonymousClient();
        reader.DefaultRequestHeaders.Add("X-Api-Key", created!.ApiKey);
        using (var refused = new HttpRequestMessage(HttpMethod.Put, "/api/security/allowed-hosts") { Content = JsonContent.Create(new UpdateAllowedHostsRequest(true, ["deluno.lan"])) })
        {
            refused.Headers.Host = "deluno.lan";
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.SendAsync(refused)).StatusCode);
        }

        Assert.Equal(0, RowsOnDisk(app, "%allowed%host%"));
        using (var save = new HttpRequestMessage(HttpMethod.Put, "/api/security/allowed-hosts") { Content = JsonContent.Create(new UpdateAllowedHostsRequest(true, ["deluno.lan"])) })
        {
            save.Headers.Host = "deluno.lan";
            Assert.Equal(HttpStatusCode.OK, (await app.Client.SendAsync(save)).StatusCode);
        }

        Assert.True(RowsOnDisk(app, "%allowed%host%") > 0, "the saved list is in the Platform database file");
        using var listed = new HttpRequestMessage(HttpMethod.Get, "/api/security/allowed-hosts");
        listed.Headers.Host = "deluno.lan";
        Assert.Equal(HttpStatusCode.OK, (await app.Client.SendAsync(listed)).StatusCode);
    }
}
