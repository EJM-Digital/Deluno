using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Platform;

/// <summary>
/// #756: the scheduled tracker-definition update's own settings and manual
/// trigger, off by default (#936) so a fresh install fetches nothing until
/// somebody turns it on.
/// </summary>
public sealed class TrackerDefinitionUpdateEndpointTests
{
    [Fact]
    public async Task A_new_install_reports_the_update_job_as_off()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.GetAsync("/api/trackers/definitions/update-check");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var state = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(state.GetProperty("isEnabled").GetBoolean());
        Assert.Equal("disabled", state.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Turning_the_job_on_and_off_is_saved()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var enable = await app.Client.PutAsJsonAsync("/api/trackers/definitions/update-check", new { isEnabled = true, intervalHours = 12 });
        Assert.True(enable.IsSuccessStatusCode, await enable.Content.ReadAsStringAsync());
        var enabledState = await enable.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(enabledState.GetProperty("isEnabled").GetBoolean());
        Assert.Equal(12, enabledState.GetProperty("intervalHours").GetInt32());

        var read = await app.Client.GetFromJsonAsync<JsonElement>("/api/trackers/definitions/update-check");
        Assert.True(read.GetProperty("isEnabled").GetBoolean());

        var disable = await app.Client.PutAsJsonAsync("/api/trackers/definitions/update-check", new { isEnabled = false, intervalHours = 12 });
        var disabledState = await disable.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(disabledState.GetProperty("isEnabled").GetBoolean());
        Assert.Equal("disabled", disabledState.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Update_now_runs_even_while_the_scheduled_switch_is_off_and_reports_a_status()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        // No internet access in the test sandbox: the real GitHub source will
        // fail, and the important, testable behaviour is that a failure never
        // throws through the endpoint -- it comes back as a normal "failed"
        // state the settings screen can show, exactly like a local setup
        // problem or an outage anywhere else in Deluno (#880, #901).
        var response = await app.Client.PostAsync("/api/trackers/definitions/update-check/run", content: null);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var state = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(state.TryGetProperty("status", out var status));
        Assert.False(string.IsNullOrWhiteSpace(status.GetString()));
    }
}
