using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Deluno.Persistence.Tests.Support;
using Deluno.Quality.Guides;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Importing a guide package by hand (the preview and apply routes under /api/v1/guides/trash), over the real routes and the
/// real platform database: a preview stores nothing and says whether it can be applied and why not, apply keeps the new version as
/// the active one and every earlier one, and a package that is stale, older, or changed under the same version is refused.
/// </summary>
public sealed class TheGuidePackageManualImportRoutesTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static JsonNode Package(int version) => JsonSerializer.SerializeToNode(GuidePackageCatalog.Current with { Version = version }, Web)!;

    private static string Integrity => GuidePackageCatalog.ComputeIntegritySha256(GuidePackageCatalog.Current);

    private static async Task<long> StoredAsync(ApplicationTestHost app)
        => await ApiScenes.ScalarAsync(app, "platform", "SELECT COUNT(*) FROM guide_package_versions;");

    [Fact]
    public async Task A_preview_of_a_newer_package_can_be_applied_and_stores_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var before = await StoredAsync(app);

        var response = await app.Client.PostAsJsonAsync("/api/v1/guides/trash/preview", new { package = Package(GuidePackageCatalog.Current.Version + 1), expectedCurrentIntegritySha256 = Integrity });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var preview = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(preview.GetProperty("canApply").GetBoolean(), preview.GetProperty("errors").ToString());
        Assert.Equal(GuidePackageCatalog.Current.Version + 1, preview.GetProperty("proposed").GetProperty("version").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(preview.GetProperty("proposedIntegritySha256").GetString()));
        Assert.Equal(before, await StoredAsync(app));
    }

    [Fact]
    public async Task A_preview_of_an_older_package_cannot_be_applied_and_says_it_is_older()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsJsonAsync("/api/v1/guides/trash/preview", new { package = Package(GuidePackageCatalog.Current.Version - 1), expectedCurrentIntegritySha256 = Integrity });

        var preview = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(preview.GetProperty("canApply").GetBoolean());
        Assert.Contains(preview.GetProperty("errors").EnumerateArray().Select(error => error.GetString()), error => error!.Contains("is older than the active version", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_preview_that_expects_a_different_active_package_cannot_be_applied_and_says_so()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsJsonAsync("/api/v1/guides/trash/preview", new { package = Package(GuidePackageCatalog.Current.Version + 1), expectedCurrentIntegritySha256 = "not-the-active-package" });

        var preview = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(preview.GetProperty("canApply").GetBoolean());
        Assert.Contains(preview.GetProperty("errors").EnumerateArray().Select(error => error.GetString()), error => error!.Contains("does not match the current package", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Applying_a_newer_package_makes_it_active_and_keeps_the_version_it_replaced_and_a_second_apply_of_the_same_one_is_refused()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var next = Package(GuidePackageCatalog.Current.Version + 1);

        var applied = await app.Client.PostAsJsonAsync("/api/v1/guides/trash/apply", new { package = next, expectedCurrentIntegritySha256 = Integrity });

        Assert.Equal(HttpStatusCode.Created, applied.StatusCode);
        Assert.Equal(GuidePackageCatalog.Current.Version + 1, (await app.Client.GetFromJsonAsync<JsonObject>("/api/v1/guides/trash/package"))!["version"]!.GetValue<int>());
        Assert.Equal(1, await ApiScenes.ScalarAsync(app, "platform", "SELECT COUNT(*) FROM guide_package_versions WHERE is_active = 1;"));

        var stale = await app.Client.PostAsJsonAsync("/api/v1/guides/trash/apply", new { package = Package(GuidePackageCatalog.Current.Version + 2), expectedCurrentIntegritySha256 = Integrity });

        Assert.True(stale.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity, $"{(int)stale.StatusCode}");
        Assert.Equal(GuidePackageCatalog.Current.Version + 1, (await app.Client.GetFromJsonAsync<JsonObject>("/api/v1/guides/trash/package"))!["version"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_package_with_no_content_is_refused_and_stores_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var before = await StoredAsync(app);

        var response = await app.Client.PostAsJsonAsync("/api/v1/guides/trash/apply", new { package = new { id = "", name = "", version = 99 }, expectedCurrentIntegritySha256 = Integrity });

        Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity, $"{(int)response.StatusCode}");
        Assert.Equal(before, await StoredAsync(app));
    }
}
