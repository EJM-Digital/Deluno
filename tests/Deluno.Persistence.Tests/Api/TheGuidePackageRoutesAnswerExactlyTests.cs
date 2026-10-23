using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Deluno.Persistence.Tests.Support;
using Deluno.Quality.Guides;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The guide package routes under /api/v1/guides/trash answer exactly what the contract says (coverage register, engines): the
/// capability inventory is the same hashed document every time, a retained version is read or answered 404, the way back to a version
/// refuses one that is not kept (404) and one whose stored row is damaged (409) without changing which package is active, the compiled
/// plan of a profile carries its hash and an unknown profile is 404, and apply answers 400 under "package" for a package that cannot
/// be applied. A stale preview is refused with the same 400, not a 409. Refs #1296.
/// </summary>
public sealed class TheGuidePackageRoutesAnswerExactlyTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private const string Base = "/api/v1/guides/trash";

    private static JsonNode Package(int version) => JsonSerializer.SerializeToNode(GuidePackageCatalog.Current with { Version = version }, Web)!;

    private static string Integrity => GuidePackageCatalog.ComputeIntegritySha256(GuidePackageCatalog.Current);

    [Fact]
    public async Task The_capability_inventory_is_the_same_hashed_document_every_time_and_names_the_active_package()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var first = await app.Client.GetStringAsync($"{Base}/inventory");
        var second = await app.Client.GetStringAsync($"{Base}/inventory");

        Assert.Equal(first, second);
        var inventory = JsonDocument.Parse(first).RootElement;
        Assert.Equal(GuidePackageCatalog.Current.Id, inventory.GetProperty("packageId").GetString());
        Assert.Equal(GuidePackageCatalog.Current.Version, inventory.GetProperty("packageVersion").GetInt32());
        Assert.Matches("^[0-9a-fA-F]{64}$", inventory.GetProperty("inventoryHash").GetString()!);
        Assert.Equal(inventory.GetProperty("totalItemCount").GetInt32(), inventory.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task A_kept_version_is_read_by_number_and_one_that_was_never_kept_is_404()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var shipped = GuidePackageCatalog.Current;
        // Versions are kept when a newer one is applied: the shipped one is kept beside it.
        (await app.Client.PostAsJsonAsync($"{Base}/apply", new { package = Package(shipped.Version + 1), expectedCurrentIntegritySha256 = Integrity })).EnsureSuccessStatusCode();

        var kept = await app.Client.GetAsync($"{Base}/versions/{shipped.Version}?packageId={shipped.Id}");
        var neverKept = await app.Client.GetAsync($"{Base}/versions/{shipped.Version + 40}?packageId={shipped.Id}");
        var otherPackage = await app.Client.GetAsync($"{Base}/versions/{shipped.Version}?packageId=not-a-package");

        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
        Assert.Equal(shipped.Version, (await kept.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("package").GetProperty("version").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, neverKept.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, otherPackage.StatusCode);
    }

    [Fact]
    public async Task Going_back_to_a_version_that_is_not_kept_is_404_and_to_one_whose_stored_row_is_damaged_is_409_and_neither_changes_the_active_package()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var shipped = GuidePackageCatalog.Current.Version;
        (await app.Client.PostAsJsonAsync($"{Base}/apply", new { package = Package(shipped + 1), expectedCurrentIntegritySha256 = Integrity })).EnsureSuccessStatusCode();

        var missing = await app.Client.PostAsync($"{Base}/versions/{shipped + 40}/activate", content: null);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        await ApiScenes.ExecuteAsync(app, "platform", $"UPDATE guide_package_versions SET package_json = '{{\"id\":\"{GuidePackageCatalog.Current.Id}\"}}' WHERE package_version = {shipped};");
        var damaged = await app.Client.PostAsync($"{Base}/versions/{shipped}/activate", content: null);

        Assert.Equal(HttpStatusCode.Conflict, damaged.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace((await damaged.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString()));
        Assert.Equal(shipped + 1, (await app.Client.GetFromJsonAsync<JsonObject>($"{Base}/package"))!["version"]!.GetValue<int>());
        Assert.Equal(1, await ApiScenes.ScalarAsync(app, "platform", "SELECT COUNT(*) FROM guide_package_versions WHERE is_active = 1;"));
    }

    [Fact]
    public async Task A_profiles_compiled_plan_carries_its_hash_and_an_unknown_profile_is_404()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var profile = GuidePackageCatalog.Current.QualityProfiles.First();

        var compiled = await app.Client.GetAsync($"{Base}/profiles/{profile.Id}/compile");
        var unknown = await app.Client.GetAsync($"{Base}/profiles/no-such-profile/compile");

        Assert.Equal(HttpStatusCode.OK, compiled.StatusCode);
        var body = await compiled.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(profile.Id, body.GetProperty("profile").GetProperty("id").GetString());
        Assert.True(body.GetProperty("advancedRules").ValueKind == JsonValueKind.Array);
        Assert.Matches("^[0-9a-fA-F]{64}$", body.GetProperty("planHash").GetString()!);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task A_package_that_cannot_be_applied_is_400_under_package_whether_it_is_older_changed_under_its_version_or_built_on_a_package_that_is_no_longer_active_and_stores_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var shipped = GuidePackageCatalog.Current.Version;
        var before = await ApiScenes.ScalarAsync(app, "platform", "SELECT COUNT(*) FROM guide_package_versions;");

        var older = await app.Client.PostAsJsonAsync($"{Base}/apply", new { package = Package(shipped - 1), expectedCurrentIntegritySha256 = Integrity });
        var sameVersionChanged = await app.Client.PostAsJsonAsync($"{Base}/apply", new
        {
            package = JsonSerializer.SerializeToNode(GuidePackageCatalog.Current with { Name = "A different name under the same version" }, Web),
            expectedCurrentIntegritySha256 = Integrity
        });
        var stale = await app.Client.PostAsJsonAsync($"{Base}/apply", new { package = Package(shipped + 1), expectedCurrentIntegritySha256 = "not-the-active-package" });

        foreach (var refused in new[] { older, sameVersionChanged, stale })
        {
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.NotEmpty((await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").GetProperty("package").EnumerateArray());
        }

        Assert.Equal(before, await ApiScenes.ScalarAsync(app, "platform", "SELECT COUNT(*) FROM guide_package_versions;"));
    }
}
