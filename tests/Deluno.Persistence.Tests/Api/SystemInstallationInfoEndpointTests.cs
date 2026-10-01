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
        Assert.Equal(Deluno.Contracts.DelunoHostName.Current, info.HostName);
    }

    [Theory]
    [InlineData("3f9a1c2b7d4e", true, true)]
    [InlineData("3f9a1c2b7d4e", false, false)]
    [InlineData("WORKSTATION", true, false)]
    [InlineData("deluno-nas", true, false)]
    [InlineData("3F9A1C2B7D4E", true, false)]
    [InlineData("3f9a1c2b7d4", true, false)]
    [InlineData("3f9a1c2b7d4eaa", true, false)]
    [InlineData("3f9a1c2b7d4g", true, false)]
    [InlineData(null, true, false)]
    public void Container_id_hostnames_are_told_apart_from_real_names(string? name, bool inContainer, bool expected)
    {
        Assert.Equal(expected, Deluno.Contracts.DelunoHostName.LooksLikeContainerId(name, inContainer));
    }

    private sealed record InfoBody(
        DateTimeOffset ProcessStartUtc,
        long UptimeSeconds,
        string OsDescription,
        string RuntimeVersion,
        string DatabaseType,
        string DatabaseLocation,
        string AppDataPath,
        string HostName,
        bool HostLooksLikeContainerId);
}
