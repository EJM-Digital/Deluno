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
/// #528: a tool that changed a library file in place tells Deluno, and Deluno
/// re-reads that one file - its size now, its streams owed a fresh probe - and
/// starts no search, upgrade, rename or import.
/// </summary>
public sealed class ExternalFileChangedTests
{
    [Fact]
    public async Task A_changed_file_is_re_read_and_nothing_is_searched()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await CreateLibraryAsync(app);
        var movieId = await AddMovieAsync(app);
        var path = Path.Combine(app.DataRoot, "movies", "Blade Runner 2049 (2017)", "Blade Runner 2049.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, new byte[1234]);

        var state = app.Services.GetRequiredService<IMediaStateRepository>();
        await state.EnsureWantedStateAsync(MediaKind.Movie, movieId, libraryId, WantedStatuses.Covered, "Imported.", hasFile: true, currentQuality: "WEBDL-1080p", targetQuality: "WEBDL-1080p", qualityCutoffMet: true, CancellationToken.None);
        await ExecuteAsync(app, DelunoDatabaseNames.Movies,
            "UPDATE movie_wanted_state SET file_path = @path, file_size_bytes = 5000, facts_probed_utc = '2026-09-01T00:00:00Z', facts_probed_size_bytes = 5000 WHERE movie_id = @id;",
            ("@path", path), ("@id", movieId));
        var jobsBefore = (await app.Services.GetRequiredService<IJobQueueRepository>().ListAsync(500, CancellationToken.None)).Select(job => job.Id).ToHashSet();

        // Told with the path in different case where the file system ignores
        // case, as Windows paths often are; exactly elsewhere (CI runs Linux).
        var told = OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
        var response = await app.Client.PostAsJsonAsync("/api/integrations/external/file-changed", new { path = told, reason = "Weir removed 2 audio tracks.", tool = "Weir" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using (var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            Assert.False(body.RootElement.GetProperty("coalesced").GetBoolean());
            Assert.Equal(movieId, body.RootElement.GetProperty("titles")[0].GetProperty("mediaId").GetString());
        }

        var (size, probed) = await ReadFileRowAsync(app, movieId);
        Assert.Equal(1234, size);
        Assert.Null(probed);

        var newJobs = (await app.Services.GetRequiredService<IJobQueueRepository>().ListAsync(500, CancellationToken.None)).Where(job => !jobsBefore.Contains(job.Id)).ToArray();
        Assert.Contains(newJobs, job => job.JobType == "library.media.probe");
        Assert.DoesNotContain(newJobs, job => job.JobType.Contains("search", StringComparison.OrdinalIgnoreCase) || job.JobType.Contains("import", StringComparison.OrdinalIgnoreCase) || job.JobType.Contains("rename", StringComparison.OrdinalIgnoreCase));

        var activity = await app.Services.GetRequiredService<IActivityFeedRepository>().ListActivityAsync(50, null, null, CancellationToken.None);
        Assert.Contains(activity, item => item.Message == "Weir changed Blade Runner 2049.mkv: Weir removed 2 audio tracks. Deluno is re-reading it.");

        // The same file again within a minute is the same change.
        var again = await app.Client.PostAsJsonAsync("/api/integrations/external/file-changed", new { path, reason = "again", tool = "Weir" });
        Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
        using var againBody = JsonDocument.Parse(await again.Content.ReadAsStringAsync());
        Assert.True(againBody.RootElement.GetProperty("coalesced").GetBoolean());
    }

    [Fact]
    public async Task A_file_Deluno_does_not_track_is_not_found()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var stray = Path.Combine(app.DataRoot, "stray.mkv");
        await File.WriteAllBytesAsync(stray, new byte[10]);

        var untracked = await app.Client.PostAsJsonAsync("/api/integrations/external/file-changed", new { path = stray, tool = "Weir" });
        var missing = await app.Client.PostAsJsonAsync("/api/integrations/external/file-changed", new { path = Path.Combine(app.DataRoot, "gone.mkv"), tool = "Weir" });
        var blank = await app.Client.PostAsJsonAsync("/api/integrations/external/file-changed", new { path = " ", tool = "Weir" });

        Assert.Equal(HttpStatusCode.NotFound, untracked.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
    }

    /// <summary>#1306: a path with ".." in it, though the file it reaches exists, and a relative path are both refused before the disk or the library is touched.</summary>
    [Fact]
    public async Task A_path_that_climbs_out_or_is_not_a_full_path_is_refused()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var folder = Path.Combine(app.DataRoot, "movies", "Some Film (2020)");
        Directory.CreateDirectory(folder);
        var real = Path.Combine(folder, "Some Film.mkv");
        await File.WriteAllBytesAsync(real, new byte[10]);

        var climbing = Path.Combine(folder, "..", "Some Film (2020)", "Some Film.mkv");
        var climbingBackslashes = climbing.Replace('/', '\\');
        var relative = Path.Combine("movies", "Some Film (2020)", "Some Film.mkv");

        foreach (var path in new[] { climbing, climbingBackslashes, relative, real + "/." })
        {
            var response = await app.Client.PostAsJsonAsync("/api/integrations/external/file-changed", new { path, tool = "Weir" });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains(SafePath.OutsideRootReason, await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task The_manifest_advertises_it()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        using var manifest = JsonDocument.Parse(await app.Client.GetStringAsync("/api/integrations/external/manifest"));

        Assert.Contains(manifest.RootElement.GetProperty("capabilities").EnumerateArray(), capability => capability.GetString() == "external-file-changed");
    }

    private static async Task<(long? Size, string? Probed)> ReadFileRowAsync(ApplicationTestHost app, string movieId)
    {
        await using var connection = await app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>().OpenConnectionAsync(DelunoDatabaseNames.Movies, CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT file_size_bytes, facts_probed_utc FROM movie_wanted_state WHERE movie_id = @id;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@id";
        parameter.Value = movieId;
        command.Parameters.Add(parameter);
        using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        Assert.True(await reader.ReadAsync(CancellationToken.None));
        return (reader.IsDBNull(0) ? null : reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    private static async Task ExecuteAsync(ApplicationTestHost app, string database, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = await app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>().OpenConnectionAsync(database, CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        Assert.Equal(1, await command.ExecuteNonQueryAsync(CancellationToken.None));
    }

    private static async Task<string> CreateLibraryAsync(ApplicationTestHost app)
    {
        var rootPath = Path.Combine(app.DataRoot, "movies");
        Directory.CreateDirectory(rootPath);
        var response = await app.Client.PostAsJsonAsync("/api/libraries/", new
        {
            name = "Movies",
            mediaType = "movies",
            purpose = "collection",
            rootPath,
            importWorkflow = "copy",
            autoSearchEnabled = false,
            missingSearchEnabled = false,
            upgradeSearchEnabled = false
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetString()!;
    }

    private static async Task<string> AddMovieAsync(ApplicationTestHost app)
    {
        var response = await app.Client.PostAsJsonAsync("/api/movies/", new { title = "Blade Runner 2049", releaseYear = 2017, monitored = true });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetString()!;
    }
}
