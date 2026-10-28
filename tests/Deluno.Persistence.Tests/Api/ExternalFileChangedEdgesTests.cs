using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Jobs.Data;
using Deluno.Media;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The edges of <c>POST /api/integrations/external/file-changed</c> that ExternalFileChangedTests does not reach: what a
/// missing or malformed body answers, and the subtitle scan the call queues once per library, however many files changed.
/// </summary>
public sealed class ExternalFileChangedEdgesTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"path\":\"   \"}")]
    public async Task A_body_with_no_path_is_a_400_that_names_the_path(string json)
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsync("/api/integrations/external/file-changed", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains("full path", body.RootElement.GetProperty("errors").GetProperty("path")[0].GetString());
    }

    [Fact]
    public async Task A_malformed_body_is_a_400_and_queues_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var before = (await jobs.ListAsync(500, CancellationToken.None)).Count;

        var response = await app.Client.PostAsync("/api/integrations/external/file-changed", new StringContent("{bad", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, (await jobs.ListAsync(500, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task One_subtitle_scan_is_queued_for_the_library_however_many_of_its_files_changed()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var rootPath = Path.Combine(app.DataRoot, "movies");
        Directory.CreateDirectory(rootPath);
        var created = await app.Client.PostAsJsonAsync("/api/libraries/", new
        {
            name = "Movies", mediaType = "movies", purpose = "collection", rootPath, importWorkflow = "copy",
            autoSearchEnabled = false, missingSearchEnabled = false, upgradeSearchEnabled = false
        });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var libraryId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var state = app.Services.GetRequiredService<IMediaStateRepository>();
        var paths = new List<string>();
        foreach (var (title, year) in new[] { ("Heat", 1995), ("Collateral", 2004) })
        {
            var added = await app.Client.PostAsJsonAsync("/api/movies/", new { title, releaseYear = year, monitored = true });
            Assert.True(added.IsSuccessStatusCode, await added.Content.ReadAsStringAsync());
            var movieId = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
            var path = Path.Combine(rootPath, $"{title} ({year})", $"{title}.mkv");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, new byte[321]);
            await state.EnsureWantedStateAsync(MediaKind.Movie, movieId, libraryId, WantedStatuses.Covered, "Imported.", hasFile: true, currentQuality: "WEBDL-1080p", targetQuality: "WEBDL-1080p", qualityCutoffMet: true, CancellationToken.None);
            await using var connection = await app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>().OpenConnectionAsync(DelunoDatabaseNames.Movies, CancellationToken.None);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE movie_wanted_state SET file_path = @path, file_size_bytes = 9 WHERE movie_id = @id;";
            foreach (var (name, value) in new[] { ("@path", path), ("@id", movieId) })
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = name;
                parameter.Value = value;
                command.Parameters.Add(parameter);
            }
            Assert.Equal(1, await command.ExecuteNonQueryAsync(CancellationToken.None));
            paths.Add(path);
        }

        foreach (var path in paths)
        {
            var told = await app.Client.PostAsJsonAsync("/api/integrations/external/file-changed", new { path, tool = "Weir" });
            Assert.Equal(HttpStatusCode.Accepted, told.StatusCode);
        }

        var scans = (await app.Services.GetRequiredService<IJobQueueRepository>().ListAsync(500, CancellationToken.None))
            .Where(job => job.JobType == "library.subtitles.scan")
            .ToArray();
        var scan = Assert.Single(scans);
        Assert.Equal(libraryId, scan.RelatedEntityId);
        Assert.Equal("library", scan.RelatedEntityType);
        Assert.Contains(libraryId, scan.PayloadJson);
    }
}
