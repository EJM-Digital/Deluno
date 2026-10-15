using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Nothing is on by default (#936), on the real host and its real new database: a new install starts with automation paused,
/// which is what the Dashboard's paused strip shows, and resuming and pausing are written to the settings and read back.
/// </summary>
public sealed class ANewInstallStartsWithAutomationPausedTests
{
    [Fact]
    public async Task A_new_install_starts_with_automation_paused_until_it_is_resumed()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var fresh = await app.Client.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.False(fresh.GetProperty("autoStartJobs").GetBoolean(), "a new install must not search or send anything by itself");

        var resumed = await app.Client.PutAsJsonAsync("/api/settings/automation", new { isEnabled = true });
        Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
        Assert.True((await app.Client.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("autoStartJobs").GetBoolean());

        var paused = await app.Client.PutAsJsonAsync("/api/settings/automation", new { isEnabled = false });
        Assert.Equal(HttpStatusCode.OK, paused.StatusCode);
        Assert.False((await app.Client.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("autoStartJobs").GetBoolean());
    }
}
