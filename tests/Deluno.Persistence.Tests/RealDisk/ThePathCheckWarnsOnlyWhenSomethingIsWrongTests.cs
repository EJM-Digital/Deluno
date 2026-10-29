using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// The folder check's warnings, over HTTP on a real temp folder: a folder that is there says nothing alarming, a path
/// neither it nor its parent can be seen names where to look, and a container path or a network share (Windows) is called what it is.
/// </summary>
public sealed class ThePathCheckWarnsOnlyWhenSomethingIsWrongTests
{
    private static async Task<JsonElement> CheckAsync(ApplicationTestHost app, string path)
    {
        var response = await app.Client.PostAsJsonAsync("/api/filesystem/path-diagnostics", new { path });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static string[] Warnings(JsonElement answer)
        => [.. answer.GetProperty("warnings").EnumerateArray().Select(item => item.GetString()!)];

    [Fact]
    public async Task A_folder_that_is_there_has_no_warning_and_one_nobody_can_see_names_where_to_look()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var folder = Directory.CreateDirectory(Path.Combine(app.DataRoot, "check-warnings")).FullName;

        var present = await CheckAsync(app, folder);
        Assert.Empty(Warnings(present));
        Assert.False(present.GetProperty("isUncPath").GetBoolean());
        Assert.False(present.GetProperty("isLikelyDockerPath").GetBoolean());
        Assert.True(present.GetProperty("parentWritable").GetBoolean());

        // The folder is not there but the one above it is: it can be made, and there is nothing to chase.
        var creatable = await CheckAsync(app, Path.Combine(folder, "soon"));
        Assert.Empty(Warnings(creatable));
        Assert.True(creatable.GetProperty("parentWritable").GetBoolean());

        // Neither it nor its parent is visible to the process.
        var invisible = await CheckAsync(app, Path.Combine(folder, "no", "such", "place"));
        var warning = Assert.Single(Warnings(invisible));
        Assert.StartsWith("Neither this folder nor the one above it is visible to the Deluno process.", warning);
        Assert.Contains("Docker volumes, UNC permissions, mapped drives, or access for", warning);
        Assert.False(invisible.GetProperty("parentWritable").GetBoolean());
        Assert.False(invisible.GetProperty("writable").GetBoolean());
        Assert.False(Directory.Exists(Path.Combine(folder, "no")), "asking creates nothing");
    }

    [Fact]
    public async Task A_network_share_or_a_container_path_is_called_what_it_is()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        if (OperatingSystem.IsWindows())
        {
            var share = await CheckAsync(app, "\\\\deluno-test-nas.invalid\\media\\Movies");
            Assert.True(share.GetProperty("isUncPath").GetBoolean(), share.ToString());
            Assert.Contains(Warnings(share), w => w.StartsWith("This is a UNC path. Give ") && w.Contains("network-share permissions"));
        }
        else
        {
            var container = await CheckAsync(app, "/downloads/deluno-test-never-mounted");
            Assert.True(container.GetProperty("isLikelyDockerPath").GetBoolean());
            Assert.Contains("This looks like a container path. Make sure the host path is mounted into the Deluno container with the same internal path.", Warnings(container));
            Assert.False(container.GetProperty("isUncPath").GetBoolean());
        }
    }
}
