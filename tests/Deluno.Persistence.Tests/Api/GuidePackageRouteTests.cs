using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// <c>GET /api/v1/guides/trash/package</c> on the real host: the active package with its version, the upstream revision it
/// is pinned to and its integrity hash, served without the large source inventory, to a key that holds read and to no one else.
/// </summary>
public sealed class GuidePackageRouteTests
{
    [Fact]
    public async Task The_package_carries_its_version_pinned_revision_and_integrity_hash_and_leaves_the_source_inventory_out()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var package = await app.Client.GetFromJsonAsync<JsonElement>("/api/v1/guides/trash/package");

        Assert.Equal("trash-guides", package.GetProperty("id").GetString());
        Assert.True(package.GetProperty("version").GetInt32() >= 1);
        Assert.True(package.GetProperty("schemaVersion").GetInt32() >= 1);
        Assert.Matches("^[0-9a-f]{64}$", package.GetProperty("integritySha256").GetString());
        Assert.Matches("^[0-9a-f]{40}$", package.GetProperty("source").GetProperty("upstreamRevision").GetString());
        Assert.Equal(JsonValueKind.Null, package.GetProperty("sourceInventory").ValueKind);
        Assert.NotEmpty(package.GetProperty("customFormats").EnumerateArray());
        Assert.NotEmpty(package.GetProperty("qualityProfiles").EnumerateArray());
        Assert.NotEmpty(package.GetProperty("qualityTiers").EnumerateArray());
    }

    [Fact]
    public async Task A_read_key_can_read_the_package_and_a_caller_with_no_key_cannot()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var created = await app.Client.PostAsJsonAsync("/api/api-keys/", new { name = "Guide reader", scopes = "read" });
        var key = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("apiKey").GetString()!;
        var reader = app.CreateAnonymousClient();
        reader.DefaultRequestHeaders.Add("X-Api-Key", key);

        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/v1/guides/trash/package")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateAnonymousClient().GetAsync("/api/v1/guides/trash/package")).StatusCode);
    }
}
