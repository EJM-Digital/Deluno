using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;
using Deluno.Quality.Contracts;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Importing and deleting custom formats (coverage register, area api), checked in the platform database file itself: an import
/// writes exactly the formats that were valid, a deleted format is gone from the file, and a second delete is a 404 that changes nothing.
/// </summary>
public sealed class CustomFormatsAreKeptInTheDatabaseFileTests
{
    private static List<string> FormatNamesOnDisk(ApplicationTestHost app)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(app.DataRoot, "platform.db")};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM custom_formats ORDER BY name;";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }

    [Fact]
    public async Task An_import_writes_the_valid_formats_to_the_database_file_reports_each_one_and_leaves_a_bad_one_out()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var payload = JsonSerializer.SerializeToElement(new object[]
        {
            new { name = "Imported WEB", mediaType = "movies", score = 50, upgradeAllowed = true, conditions = new[] { new { type = "source", value = "web", required = true } } },
            new { name = "Imported Remux", mediaType = "movies", score = 80, upgradeAllowed = true, conditions = new[] { new { type = "source", value = "remux", required = true } } },
            new { name = "", mediaType = "movies", score = 1, upgradeAllowed = true, conditions = new[] { new { type = "source", value = "web", required = true } } }
        });

        var response = await app.Client.PostAsJsonAsync("/api/custom-formats/import", payload);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var results = await response.Content.ReadFromJsonAsync<CustomFormatImportResult[]>();
        Assert.Equal(3, results!.Length);
        Assert.Equal(2, results.Count(result => result.Imported));
        Assert.False(results[2].Imported);
        Assert.Equal(new List<string> { "Imported Remux", "Imported WEB" }, FormatNamesOnDisk(app).Where(name => name.StartsWith("Imported", StringComparison.Ordinal)).ToList());
    }

    [Fact]
    public async Task Deleting_a_custom_format_removes_its_row_from_the_database_file_and_only_that_one_and_saying_it_twice_is_404()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        async Task<string> AddAsync(string name)
        {
            var created = await app.Client.PostAsJsonAsync("/api/custom-formats", new CreateCustomFormatRequest(name, "movies", 10, null, "[{\"type\":\"source\",\"value\":\"web\",\"required\":true}]", true));
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
            return (await created.Content.ReadFromJsonAsync<CustomFormatItem>())!.Id;
        }

        var doomed = await AddAsync("Doomed format");
        await AddAsync("Kept format");
        Assert.Contains("Doomed format", FormatNamesOnDisk(app));

        var removed = await app.Client.DeleteAsync($"/api/custom-formats/{doomed}");

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.DoesNotContain("Doomed format", FormatNamesOnDisk(app));
        Assert.Contains("Kept format", FormatNamesOnDisk(app));
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync($"/api/custom-formats/{doomed}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync($"/api/custom-formats/{doomed}/export")).StatusCode);
    }
}
