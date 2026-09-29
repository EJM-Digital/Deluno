using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// One status address with version and uptime, for a dashboard such as
/// Homepage (#1020). Field names are Radarr's <c>SystemResource</c>.
/// </summary>
public sealed class SystemStatusEndpointTests
{
    [Fact]
    public async Task Status_carries_the_version_and_uptime_in_Radarrs_field_names_plus_health_updates_and_tasks()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.GetAsync("/api/system/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var status = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Deluno", status.GetProperty("appName").GetString());
        Assert.False(string.IsNullOrWhiteSpace(status.GetProperty("version").GetString()));
        Assert.True(status.GetProperty("uptimeSeconds").GetInt64() >= 0);
        Assert.Equal(JsonValueKind.String, status.GetProperty("startTime").ValueKind);
        Assert.Equal("sqLite", status.GetProperty("databaseType").GetString());
        Assert.False(string.IsNullOrWhiteSpace(status.GetProperty("databaseVersion").GetString()));
        Assert.True(status.GetProperty("isNetCore").GetBoolean());
        Assert.Equal(OperatingSystem.IsWindows(), status.GetProperty("isWindows").GetBoolean());
        Assert.Equal(app.DataRoot, status.GetProperty("appData").GetString(), ignoreCase: true);
        Assert.Contains(status.GetProperty("health").GetProperty("status").GetString(), new[] { "healthy", "attention" });
        Assert.Equal(JsonValueKind.String, status.GetProperty("updates").GetProperty("state").ValueKind);
        Assert.True(status.GetProperty("tasks").GetProperty("total").GetInt32() > 0);
    }

    [Fact]
    public async Task Status_agrees_with_the_installation_info_it_shares_a_reading_with()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var status = await app.Client.GetFromJsonAsync<JsonElement>("/api/system/status");
        var info = await app.Client.GetFromJsonAsync<JsonElement>("/api/system/info");

        Assert.Equal(info.GetProperty("processStartUtc").GetDateTimeOffset(), status.GetProperty("startTime").GetDateTimeOffset());
        Assert.Equal(info.GetProperty("appDataPath").GetString(), status.GetProperty("appData").GetString());
    }

    [Fact]
    public async Task Status_needs_a_sign_in()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        using var anonymous = app.CreateAnonymousClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/system/status")).StatusCode);
    }

    [Fact]
    public async Task A_read_key_can_read_status()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var created = await app.Client.PostAsJsonAsync("/api/api-keys/", new { name = "Dashboard", scopes = "read" });
        var key = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("apiKey").GetString()!;
        using var dashboard = app.CreateAnonymousClient();
        dashboard.DefaultRequestHeaders.Add("X-Api-Key", key);

        Assert.Equal(HttpStatusCode.OK, (await dashboard.GetAsync("/api/system/status")).StatusCode);
    }
}
