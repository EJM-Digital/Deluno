using System.Net;
using System.Net.Http.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #867: a Log Files list with download and open-folder actions, and a
/// delete action that refuses to remove the file still being written.
/// </summary>
public sealed class SystemLogFilesEndpointTests
{
    [Fact]
    public async Task Lists_files_actually_present_in_the_logs_directory()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var logDirectory = Path.Combine(app.DataRoot, "logs");
        Directory.CreateDirectory(logDirectory);
        await File.WriteAllTextAsync(Path.Combine(logDirectory, "deluno-2026-09-01.jsonl"), "{\"a\":1}\n");
        await File.WriteAllTextAsync(Path.Combine(logDirectory, "deluno-2026-09-02.jsonl"), "{\"b\":2}\n{\"c\":3}\n");
        await File.WriteAllTextAsync(Path.Combine(logDirectory, "not-a-log.txt"), "ignore me");

        var files = await app.Client.GetFromJsonAsync<List<LogFileBody>>("/api/system/log-files");

        Assert.NotNull(files);
        Assert.Contains(files, file => file.Name == "deluno-2026-09-01.jsonl");
        Assert.Contains(files, file => file.Name == "deluno-2026-09-02.jsonl");
        Assert.DoesNotContain(files, file => file.Name == "not-a-log.txt");
    }

    [Fact]
    public async Task Download_returns_the_correct_files_bytes()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var logDirectory = Path.Combine(app.DataRoot, "logs");
        Directory.CreateDirectory(logDirectory);
        var content = "{\"level\":\"Information\",\"message\":\"hello\"}\n";
        await File.WriteAllTextAsync(Path.Combine(logDirectory, "deluno-2026-09-03.jsonl"), content);

        var response = await app.Client.GetAsync("/api/system/log-files/deluno-2026-09-03.jsonl/download");

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal(content, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Download_refuses_a_name_outside_the_logs_directory()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.GetAsync("/api/system/log-files/..%2f..%2fsecrets.json/download");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_removes_an_older_log_file()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var logDirectory = Path.Combine(app.DataRoot, "logs");
        Directory.CreateDirectory(logDirectory);
        var path = Path.Combine(logDirectory, "deluno-2026-01-01.jsonl");
        await File.WriteAllTextAsync(path, "{}\n");

        var response = await app.Client.DeleteAsync("/api/system/log-files/deluno-2026-01-01.jsonl");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Delete_refuses_todays_file_still_being_written()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var logDirectory = Path.Combine(app.DataRoot, "logs");
        Directory.CreateDirectory(logDirectory);
        var todayName = $"deluno-{DateTimeOffset.UtcNow:yyyy-MM-dd}.jsonl";
        var path = Path.Combine(logDirectory, todayName);
        await File.WriteAllTextAsync(path, "{}\n");

        var response = await app.Client.DeleteAsync($"/api/system/log-files/{todayName}");

        Assert.False(response.IsSuccessStatusCode);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Folder_endpoint_returns_the_logs_directory_path()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var folder = await app.Client.GetFromJsonAsync<FolderBody>("/api/system/log-files/folder");

        Assert.NotNull(folder);
        Assert.Equal(Path.Combine(app.DataRoot, "logs"), folder!.Path);
    }

    private sealed record LogFileBody(string Name, long SizeBytes, DateTimeOffset LastWrittenUtc);

    private sealed record FolderBody(string Path);
}
