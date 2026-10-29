using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Infrastructure.Storage;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The disk-space address on real folders: a drive that cannot be read is left out of the list and never reported as zero
/// bytes, and a library folder that does not exist yet is read through its nearest existing parent, so it is neither an error
/// nor a missing drive.
/// </summary>
public sealed class DiskSpaceLeavesOutWhatItCannotReadTests
{
    private static Task AddLibraryAsync(ApplicationTestHost app, string name, string root, string? downloads = null)
        => app.Services.GetRequiredService<ILibrariesRepository>().CreateLibraryAsync(
            new CreateLibraryRequest(
                Name: name, MediaType: "movies", Purpose: "collection", RootPath: root, DownloadsPath: downloads, QualityProfileId: null,
                ImportWorkflow: "standard", ProcessorName: null, ProcessorOutputPath: null, ProcessorTimeoutMinutes: null, ProcessorFailureMode: null,
                AutoSearchEnabled: false, MissingSearchEnabled: false, UpgradeSearchEnabled: false, SearchIntervalMinutes: null,
                RetryDelayHours: null, MaxItemsPerRun: null),
            CancellationToken.None);

    [Fact]
    public async Task A_folder_that_cannot_be_read_is_left_out_and_no_drive_is_listed_with_nothing_on_it()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName;
        await AddLibraryAsync(app, "Films", root, "not\0a path");

        using var response = await app.Client.GetAsync("/api/diskspace");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var drives = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(drives.GetArrayLength() >= 1);
        foreach (var drive in drives.EnumerateArray())
        {
            Assert.DoesNotContain("not\0a path", drive.GetProperty("path").GetString());
            Assert.NotEqual("Films downloads", drive.GetProperty("label").GetString());
            Assert.True(drive.GetProperty("totalSpace").GetInt64() > 0);
        }
    }

    [Fact]
    public async Task A_library_folder_that_does_not_exist_yet_is_read_through_its_nearest_existing_parent()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var parent = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Shelf")).FullName;
        var notYet = Path.Combine(parent, "Films", "Later");
        await AddLibraryAsync(app, "Films", notYet);

        using var response = await app.Client.GetAsync("/api/diskspace");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(Directory.Exists(notYet));
        var inParent = VolumeSpace.TryRead(parent);
        var reading = VolumeSpace.TryRead(notYet);
        Assert.NotNull(inParent);
        Assert.NotNull(reading);
        Assert.Equal(inParent!.VolumeKey, reading!.VolumeKey);
        Assert.Equal(inParent.TotalBytes, reading.TotalBytes);
        var drives = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.All(drives.EnumerateArray(), drive => Assert.True(drive.GetProperty("totalSpace").GetInt64() > 0));
    }
}
