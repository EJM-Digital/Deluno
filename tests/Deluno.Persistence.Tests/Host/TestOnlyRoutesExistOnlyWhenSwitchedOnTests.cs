using System.Net;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Host;

/// <summary>
/// The routes only the browser tests use (seed a large library, push a realtime event, seed Transfers) are absent unless
/// their Testing switch is on: a shipped install has none of them, not even as an unauthorized answer.
/// </summary>
public sealed class TestOnlyRoutesExistOnlyWhenSwitchedOnTests
{
    public static TheoryData<string, string> Switches => new()
    {
        { "Testing:EnableLargeLibrarySeedEndpoint", "/api/testing/movies/unseed-large-library" },
        { "Testing:EnableRealtimePushEndpoint", "/api/testing/realtime/activity" },
        { "Testing:EnableTransfersSeedEndpoint", "/api/testing/transfers/clear" },
    };

    [Theory]
    [MemberData(nameof(Switches))]
    public async Task The_route_is_absent_while_its_switch_is_off(string _, string route)
    {
        await using var app = await ApplicationTestHost.StartAsync();

        using var response = await app.Client.PostAsync(route, null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Switches))]
    public async Task The_route_is_served_once_its_switch_is_on(string key, string route)
    {
        await using var app = await ApplicationTestHost.StartAsync(settings: new Dictionary<string, string?> { [key] = "true" });

        using var response = await app.Client.PostAsync(route, null);

        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
    }
}
