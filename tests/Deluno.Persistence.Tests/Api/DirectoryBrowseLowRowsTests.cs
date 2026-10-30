using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// GET /api/filesystem/directories (#1329) over real folders in a temp data root: with no path it lists the roots, with a path it
/// lists only that folder's sub-folders (never its files), sorted, with the parent; a missing folder is a 404 and one the process
/// cannot read is a 403.
/// </summary>
public sealed class DirectoryBrowseLowRowsTests
{
    [Fact]
    public async Task With_no_path_it_lists_the_roots_and_has_neither_a_current_folder_nor_a_parent()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.GetAsync("/api/filesystem/directories");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, body.GetProperty("currentPath").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("parentPath").ValueKind);
        var entries = body.GetProperty("entries").EnumerateArray().ToArray();
        Assert.NotEmpty(entries);
        if (OperatingSystem.IsWindows())
        {
            Assert.Contains(entries, entry => entry.GetProperty("kind").GetString() == "root" && entry.GetProperty("path").GetString()!.EndsWith(":\\", StringComparison.Ordinal));
        }
        else
        {
            Assert.Contains(entries, entry => entry.GetProperty("path").GetString() == "/");
        }
    }

    [Fact]
    public async Task With_a_path_it_lists_only_that_folders_sub_folders_sorted_with_its_parent_and_never_its_files()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var browse = Directory.CreateDirectory(Path.Combine(app.DataRoot, "browse")).FullName;
        foreach (var name in new[] { "charlie", "Alpha", "bravo" })
        {
            Directory.CreateDirectory(Path.Combine(browse, name));
        }

        await File.WriteAllTextAsync(Path.Combine(browse, "a-file.mkv"), "not a folder");

        var response = await app.Client.GetAsync($"/api/filesystem/directories?path={Uri.EscapeDataString(browse)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(Path.GetFullPath(browse).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(body.GetProperty("currentPath").GetString()!).TrimEnd(Path.DirectorySeparatorChar));
        Assert.Equal(Path.GetFullPath(app.DataRoot).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(body.GetProperty("parentPath").GetString()!).TrimEnd(Path.DirectorySeparatorChar));
        var entries = body.GetProperty("entries").EnumerateArray().ToArray();
        Assert.Equal(["Alpha", "bravo", "charlie"], entries.Select(entry => entry.GetProperty("name").GetString()!).ToArray());
        Assert.All(entries, entry => Assert.Equal("directory", entry.GetProperty("kind").GetString()));
    }

    [Fact]
    public async Task A_folder_the_process_cannot_read_is_a_403()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // reason: taking away a folder's read permission needs an ACL change; the Unix mode bits below are the portable way to do it
        }

        await using var app = await ApplicationTestHost.StartAsync();
        var locked = Directory.CreateDirectory(Path.Combine(app.DataRoot, "locked")).FullName;
        Directory.CreateDirectory(Path.Combine(locked, "inside"));
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            try
            {
                _ = Directory.GetDirectories(locked);
                return; // reason: a process running as root can read any folder, so there is nothing to refuse
            }
            catch (UnauthorizedAccessException)
            {
                // The folder is closed to this process, as the test needs.
            }

            var response = await app.Client.GetAsync($"/api/filesystem/directories?path={Uri.EscapeDataString(locked)}");

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
