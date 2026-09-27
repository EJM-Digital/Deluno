using System.Net.Http.Json;
using Deluno.Infrastructure.Observability;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #866: a Log Level control that takes effect while Deluno is running,
/// not only at the next start.
/// </summary>
public sealed class SystemLogLevelEndpointTests
{
    [Fact]
    public async Task Defaults_to_Information()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.GetFromJsonAsync<LevelBody>("/api/system/log-level");

        Assert.Equal("Information", response!.Level);
    }

    [Fact]
    public async Task Setting_the_level_takes_effect_immediately_and_is_read_back()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PutAsJsonAsync("/api/system/log-level", new { level = "Debug" });
        Assert.True(response.IsSuccessStatusCode);

        var body = await response.Content.ReadFromJsonAsync<LevelBody>();
        Assert.Equal("Debug", body!.Level);

        var store = app.Services.GetRequiredService<IRuntimeLogLevelStore>();
        Assert.Equal(LogLevel.Debug, store.Current);

        var read = await app.Client.GetFromJsonAsync<LevelBody>("/api/system/log-level");
        Assert.Equal("Debug", read!.Level);
    }

    [Fact]
    public async Task An_unrecognised_level_is_refused()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PutAsJsonAsync("/api/system/log-level", new { level = "Chatty" });

        Assert.False(response.IsSuccessStatusCode);
    }

    private sealed record LevelBody(string Level);
}
