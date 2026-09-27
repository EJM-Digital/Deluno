using System.Net.Http.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #869: uptime, runtime, and app-data details for the System screen's
/// "This installation" card, read live rather than hardcoded.
/// </summary>
public sealed class SystemInstallationInfoEndpointTests
{
    [Fact]
    public async Task Returns_plausible_live_values()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var info = await app.Client.GetFromJsonAsync<InfoBody>("/api/system/info");

        Assert.NotNull(info);
        Assert.True(info!.UptimeSeconds >= 0);
        Assert.True(info.ProcessStartUtc <= DateTimeOffset.UtcNow);
        Assert.False(string.IsNullOrWhiteSpace(info.OsDescription));
        Assert.False(string.IsNullOrWhiteSpace(info.RuntimeVersion));
        Assert.Equal("SQLite", info.DatabaseType);
        Assert.Equal(Path.GetFullPath(app.DataRoot), info.DatabaseLocation);
        Assert.Equal(Path.GetFullPath(app.DataRoot), info.AppDataPath);
    }

    private sealed record InfoBody(
        DateTimeOffset ProcessStartUtc,
        long UptimeSeconds,
        string OsDescription,
        string RuntimeVersion,
        string DatabaseType,
        string DatabaseLocation,
        string AppDataPath);
}
