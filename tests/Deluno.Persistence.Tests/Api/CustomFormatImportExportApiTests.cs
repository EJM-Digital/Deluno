using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;
using Deluno.Quality.Contracts;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// JSON import/export for individual and bulk custom formats (#777), and the
/// import report of any condition it could not interpret (#774).
/// </summary>
public sealed class CustomFormatImportExportApiTests
{
    /// <summary>#1280: the import takes one format (an object) or a list of them; anything else is not the documented shape and is refused, not answered with an error per "item".</summary>
    [Theory]
    [InlineData("12345")]
    [InlineData("\"just some text\"")]
    [InlineData("true")]
    [InlineData("null")]
    public async Task Import_refuses_a_body_that_is_not_a_format_or_a_list_of_formats(string json)
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsync(
            "/api/custom-formats/import",
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("JSON object", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task A_single_format_round_trips_through_export_then_import()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var created = await app.Client.PostAsJsonAsync(
            "/api/custom-formats",
            new CreateCustomFormatRequest(
                $"Round trip {Guid.NewGuid():N}",
                "movies",
                100,
                null,
                "[{\"type\":\"source\",\"value\":\"web\",\"required\":true}]",
                true));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var original = await created.Content.ReadFromJsonAsync<CustomFormatItem>();

        var exported = await app.Client.GetAsync($"/api/custom-formats/{original!.Id}/export");
        Assert.Equal(HttpStatusCode.OK, exported.StatusCode);
        var exportBody = await exported.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(original.Name, exportBody.GetProperty("name").GetString());
        Assert.Single(exportBody.GetProperty("conditions").EnumerateArray());

        // Re-import it under a new name (the original name is still taken).
        var reimportPayload = JsonSerializer.SerializeToElement(new
        {
            name = original.Name + " (copy)",
            mediaType = original.MediaType,
            score = original.Score,
            upgradeAllowed = original.UpgradeAllowed,
            conditions = exportBody.GetProperty("conditions")
        });
        var imported = await app.Client.PostAsJsonAsync("/api/custom-formats/import", reimportPayload);
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        var results = await imported.Content.ReadFromJsonAsync<CustomFormatImportResult[]>();
        var result = Assert.Single(results!);
        Assert.True(result.Imported);
        Assert.Empty(result.DroppedConditions);
        Assert.NotNull(result.Id);
    }

    [Fact]
    public async Task A_bulk_export_returns_every_saved_format()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var name = $"Bulk export {Guid.NewGuid():N}";
        await app.Client.PostAsJsonAsync(
            "/api/custom-formats",
            new CreateCustomFormatRequest(name, "movies", 50, null, "[{\"type\":\"hdr\",\"value\":\"HDR10\",\"required\":true}]", true));

        var exported = await app.Client.GetAsync("/api/custom-formats/export");
        Assert.Equal(HttpStatusCode.OK, exported.StatusCode);
        var items = await exported.Content.ReadFromJsonAsync<JsonElement[]>();
        Assert.Contains(items!, item => item.GetProperty("name").GetString() == name);
    }

    [Fact]
    public async Task A_bulk_import_creates_every_valid_format_and_reports_each_outcome()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var nameA = $"Bulk import A {Guid.NewGuid():N}";
        var nameB = $"Bulk import B {Guid.NewGuid():N}";
        var payload = JsonSerializer.SerializeToElement(new object[]
        {
            new { name = nameA, mediaType = "movies", score = 10, conditions = new[] { new { type = "codec", value = "x265", required = true } } },
            new { name = nameB, mediaType = "movies", score = 20, conditions = new[] { new { type = "hdr", value = "HDR10", required = true } } }
        });

        var response = await app.Client.PostAsJsonAsync("/api/custom-formats/import", payload);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var results = await response.Content.ReadFromJsonAsync<CustomFormatImportResult[]>();
        Assert.Equal(2, results!.Length);
        Assert.All(results, result => Assert.True(result.Imported));

        var list = await app.Client.GetFromJsonAsync<CustomFormatItem[]>("/api/custom-formats");
        Assert.Contains(list!, item => item.Name == nameA);
        Assert.Contains(list!, item => item.Name == nameB);
    }

    /// <summary>
    /// #774: an unsupported Quality Modifier value (or any other condition
    /// this matcher cannot interpret) is dropped from what gets saved, but
    /// always named in the report rather than silently lost.
    /// </summary>
    [Fact]
    public async Task Import_reports_a_dropped_condition_instead_of_silently_losing_it()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var name = $"Partially supported {Guid.NewGuid():N}";
        var payload = JsonSerializer.SerializeToElement(new
        {
            name,
            mediaType = "movies",
            score = 10,
            conditions = new object[]
            {
                new { implementation = "SourceSpecification", fields = new[] { new { name = "value", value = "web-dl" } } },
                new { implementation = "QualityModifierSpecification", fields = new[] { new { name = "value", value = "99" } } }
            }
        });

        var response = await app.Client.PostAsJsonAsync("/api/custom-formats/import", payload);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = Assert.Single((await response.Content.ReadFromJsonAsync<CustomFormatImportResult[]>())!);
        Assert.True(result.Imported);
        var dropped = Assert.Single(result.DroppedConditions);
        Assert.Contains("QualityModifierSpecification", dropped, StringComparison.OrdinalIgnoreCase);

        var list = await app.Client.GetFromJsonAsync<CustomFormatItem[]>("/api/custom-formats");
        var saved = Assert.Single(list!, item => item.Name == name);
        Assert.Contains("source", saved.Conditions, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("qualitymodifier", saved.Conditions, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Import_rejects_a_duplicate_name_within_the_same_batch_without_touching_the_first()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var name = $"Duplicate in batch {Guid.NewGuid():N}";
        var payload = JsonSerializer.SerializeToElement(new object[]
        {
            new { name, mediaType = "movies", score = 10, conditions = new[] { new { type = "codec", value = "x265", required = true } } },
            new { name, mediaType = "movies", score = 20, conditions = new[] { new { type = "hdr", value = "HDR10", required = true } } }
        });

        var response = await app.Client.PostAsJsonAsync("/api/custom-formats/import", payload);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var results = await response.Content.ReadFromJsonAsync<CustomFormatImportResult[]>();
        Assert.True(results![0].Imported);
        Assert.False(results[1].Imported);
        Assert.Contains("already exists", results[1].Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Import_reports_a_format_with_no_understandable_condition_rather_than_saving_an_empty_rule()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var payload = JsonSerializer.SerializeToElement(new
        {
            name = $"Nothing understood {Guid.NewGuid():N}",
            mediaType = "movies",
            score = 10,
            conditions = new object[]
            {
                new { implementation = "LanguageSpecification", fields = new[] { new { name = "value", value = "english" } } }
            }
        });

        var response = await app.Client.PostAsJsonAsync("/api/custom-formats/import", payload);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = Assert.Single((await response.Content.ReadFromJsonAsync<CustomFormatImportResult[]>())!);
        Assert.False(result.Imported);
        Assert.Null(result.Id);
        Assert.NotEmpty(result.DroppedConditions);
    }
}
