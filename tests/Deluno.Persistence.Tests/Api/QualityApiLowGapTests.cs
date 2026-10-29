using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Persistence.Tests.Support;
using Deluno.Quality.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The low-severity rows of the quality area (#1329): quality profile defaults and pushes, the custom-format routes' small
/// answers and the media plan delete, all over HTTP against the whole application with the pushes recorded.
/// </summary>
public sealed class QualityApiLowGapTests
{
    private sealed class Pushes : NullRealtimeEventPublisher
    {
        public List<(string Type, string Id)> Changed { get; } = [];

        public override Task PublishEntityChangedAsync(string entityType, string entityId, CancellationToken cancellationToken)
        {
            lock (Changed) Changed.Add((entityType, entityId));
            return Task.CompletedTask;
        }
    }

    private static async Task<(ApplicationTestHost App, Pushes Pushes)> StartAsync()
    {
        var pushes = new Pushes();
        var app = await ApplicationTestHost.StartAsync(replaceServices: services =>
            services.Replace(ServiceDescriptor.Singleton<IRealtimeEventPublisher>(pushes)));
        return (app, pushes);
    }

    private static CreateQualityProfileRequest Profile(string mediaType, string? allowed = null, string? cutoff = null)
        => new($"Low gap {Guid.NewGuid():N}", mediaType, cutoff ?? "WEB 1080p", allowed, null, UpgradeUntilCutoff: true, UpgradeUnknownItems: false);

    [Fact]
    public async Task A_blank_allowed_list_on_create_takes_the_media_types_default_tiers_and_the_flags_stay_unwritten()
    {
        var (app, _) = await StartAsync();
        await using var _a = app;

        var movies = await (await app.Client.PostAsJsonAsync("/api/quality-profiles", Profile("movies"))).Content.ReadFromJsonAsync<QualityProfileItem>();
        var tv = await (await app.Client.PostAsJsonAsync("/api/quality-profiles", Profile("tv"))).Content.ReadFromJsonAsync<QualityProfileItem>();

        Assert.Equal("WEB 1080p,Bluray 1080p,Remux 1080p", movies!.AllowedQualities.Replace(", ", ","));
        Assert.Equal("WEB 720p,WEB 1080p,HDTV 1080p", tv!.AllowedQualities.Replace(", ", ","));
        foreach (var item in new[] { movies, tv })
        {
            Assert.False(item.AllowLowerQualityReplacements);
            Assert.Null(item.PresetId);
            Assert.Null(item.PresetVersion);
            Assert.False(item.PresetDrifted);
        }
    }

    [Fact]
    public async Task A_cutoff_that_is_not_in_the_profiles_allowed_list_is_accepted_and_kept()
    {
        var (app, _) = await StartAsync();
        await using var _a = app;

        var response = await app.Client.PostAsJsonAsync("/api/quality-profiles", Profile("movies", allowed: "WEB 720p,WEB 1080p", cutoff: "Remux 2160p"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var item = await response.Content.ReadFromJsonAsync<QualityProfileItem>();
        Assert.Equal("Remux 2160p", item!.CutoffQuality);
        Assert.DoesNotContain("Remux 2160p", item.AllowedQualities);
    }

    [Fact]
    public async Task Putting_a_profile_that_does_not_exist_is_404_and_pushes_nothing_while_put_and_delete_of_a_real_one_push_its_id()
    {
        var (app, pushes) = await StartAsync();
        await using var _a = app;

        var missing = await app.Client.PutAsJsonAsync("/api/quality-profiles/no-such-profile",
            new UpdateQualityProfileRequest("Nope", "WEB 1080p", "WEB 1080p", null, true, false));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        lock (pushes.Changed) Assert.Empty(pushes.Changed);

        var created = await (await app.Client.PostAsJsonAsync("/api/quality-profiles", Profile("movies"))).Content.ReadFromJsonAsync<QualityProfileItem>();
        lock (pushes.Changed) pushes.Changed.Clear();

        var updated = await app.Client.PutAsJsonAsync($"/api/quality-profiles/{created!.Id}",
            new UpdateQualityProfileRequest("Renamed", "WEB 1080p", "WEB 1080p", null, true, false));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        lock (pushes.Changed) Assert.Equal([("QualityProfile", created.Id)], pushes.Changed);

        lock (pushes.Changed) pushes.Changed.Clear();
        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.DeleteAsync($"/api/quality-profiles/{created.Id}")).StatusCode);
        lock (pushes.Changed) Assert.Equal([("QualityProfile", created.Id)], pushes.Changed);
    }

    private static async Task<CustomFormatItem> CreateFormatAsync(HttpClient client, string? trashId = null, string conditions = "[{\"type\":\"source\",\"value\":\"web\",\"required\":true}]")
    {
        var response = await client.PostAsJsonAsync("/api/custom-formats",
            new CreateCustomFormatRequest($"Low gap {Guid.NewGuid():N}", "movies", 10, trashId, conditions, true));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CustomFormatItem>())!;
    }

    [Fact]
    public async Task Custom_format_put_and_export_of_an_unknown_id_are_404_and_a_blank_guide_id_on_put_keeps_the_stored_one()
    {
        var (app, _) = await StartAsync();
        await using var _a = app;

        var put = await app.Client.PutAsJsonAsync("/api/custom-formats/no-such-format",
            new UpdateCustomFormatRequest("Whatever", "movies", 1, null, "[]", true));
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync("/api/custom-formats/no-such-format/export")).StatusCode);

        var created = await CreateFormatAsync(app.Client, trashId: "abc123guide");
        Assert.Equal("abc123guide", created.TrashId);
        var updated = await app.Client.PutAsJsonAsync($"/api/custom-formats/{created.Id}",
            new UpdateCustomFormatRequest(created.Name + " edited", "movies", 20, "", created.Conditions, true));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var item = await updated.Content.ReadFromJsonAsync<CustomFormatItem>();
        Assert.Equal("abc123guide", item!.TrashId);
        Assert.Equal(20, item.Score);
    }

    [Fact]
    public async Task Posting_a_guide_id_the_package_does_not_contain_is_refused_with_a_trash_id_problem()
    {
        var (app, _) = await StartAsync();
        await using var _a = app;

        var response = await app.Client.PostAsJsonAsync("/api/custom-formats",
            new CreateCustomFormatRequest("From a guide", "movies", 0, "not-in-the-package-at-all", null, true));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("not-in-the-package-at-all", body.GetProperty("errors").GetProperty("trashId")[0].GetString());
    }

    [Fact]
    public async Task Import_refuses_a_name_in_the_library_and_reports_each_bad_item_in_plain_words()
    {
        var (app, _) = await StartAsync();
        await using var _a = app;
        var existing = await CreateFormatAsync(app.Client);

        var body = JsonSerializer.SerializeToElement(new object[]
        {
            new { name = existing.Name, mediaType = "movies", conditions = new[] { new { type = "source", value = "web", required = true } } },
            5,
            new { name = "", conditions = new[] { new { type = "source", value = "web", required = true } } },
            new { name = "No conditions here", mediaType = "movies" }
        });
        var response = await app.Client.PostAsJsonAsync("/api/custom-formats/import", body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var results = (await response.Content.ReadFromJsonAsync<CustomFormatImportResult[]>())!;
        Assert.Equal(4, results.Length);
        Assert.All(results, result => Assert.False(result.Imported));
        Assert.Equal($"A movies custom rule named '{existing.Name}' already exists. Rename it before importing, or edit that rule instead.", results[0].Error);
        Assert.Equal("Each imported item must be a JSON object.", results[1].Error);
        Assert.Equal("Give this custom format a name.", results[2].Error);
        Assert.Equal("No conditions were supplied.", results[3].Error);
    }

    [Fact]
    public async Task Import_of_an_empty_array_is_400_nothing_to_import()
    {
        var (app, _) = await StartAsync();
        await using var _a = app;

        var response = await app.Client.PostAsync("/api/custom-formats/import", new StringContent("[]", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Nothing to import.", body.GetProperty("errors").GetProperty("conditions")[0].GetString());
    }

    [Fact]
    public async Task Import_reads_score_upgrade_allowed_and_media_type_and_defaults_them_to_0_true_and_movies()
    {
        var (app, _) = await StartAsync();
        await using var _a = app;
        var conditions = new[] { new { type = "source", value = "web", required = true } };
        var name1 = $"Imported {Guid.NewGuid():N}";
        var name2 = $"Imported {Guid.NewGuid():N}";

        var response = await app.Client.PostAsJsonAsync("/api/custom-formats/import", JsonSerializer.SerializeToElement(new object[]
        {
            new { name = name1, mediaType = "tv", score = 75, upgradeAllowed = false, conditions },
            new { name = name2, conditions }
        }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var results = (await response.Content.ReadFromJsonAsync<CustomFormatImportResult[]>())!;
        Assert.All(results, result => Assert.True(result.Imported));
        var saved = (await app.Client.GetFromJsonAsync<CustomFormatItem[]>("/api/custom-formats"))!;
        var first = saved.Single(item => item.Name == name1);
        var second = saved.Single(item => item.Name == name2);
        Assert.Equal(("tv", 75, false), (first.MediaType, first.Score, first.UpgradeAllowed));
        Assert.Equal(("movies", 0, true), (second.MediaType, second.Score, second.UpgradeAllowed));
    }

    [Fact]
    public async Task Deleting_a_media_plan_pushes_its_id_and_a_second_delete_is_404()
    {
        var (app, pushes) = await StartAsync();
        await using var _a = app;
        var created = await app.Client.PostAsJsonAsync("/api/policy-sets",
            new CreatePolicySetRequest($"Plan {Guid.NewGuid():N}", "movies", null, null, null, null, null, true, true, null));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var plan = (await created.Content.ReadFromJsonAsync<PolicySetItem>())!;
        lock (pushes.Changed) pushes.Changed.Clear();

        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.DeleteAsync($"/api/policy-sets/{plan.Id}")).StatusCode);
        lock (pushes.Changed) Assert.Equal([("PolicySet", plan.Id)], pushes.Changed);

        lock (pushes.Changed) pushes.Changed.Clear();
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync($"/api/policy-sets/{plan.Id}")).StatusCode);
        lock (pushes.Changed) Assert.Empty(pushes.Changed);
    }
}
