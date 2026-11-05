using Deluno.Infrastructure.Storage;
using Deluno.Libraries.Contracts;
using Deluno.Worker.ApiParity;

namespace Deluno.Persistence.Tests.Api;

/// <summary>The words each drive in GET /api/diskspace is given: Deluno's own data, the library, "&lt;library&gt; downloads".</summary>
public sealed class DiskSpaceLabelsTests
{
    private static LibraryItem Library(string name, string root, string? downloads)
    {
        var now = DateTimeOffset.UnixEpoch;
        return new LibraryItem(
            "id-" + name, name, "movies", "main", root, downloads, null, null, null,
            true, true, "direct", null, null, 0, "block",
            false, false, false, 6, 6, 10, null, null, "active", false, null, null, now, now);
    }

    // Each folder starts with its drive letter in these examples: the drive is the first segment.
    private static VolumeSpaceReading? Read(string? path)
        => path is null or "" or "Z:/gone" ? null : new VolumeSpaceReading(path[..2], 100, 1000);

    [Fact]
    public void A_library_and_its_downloads_folder_on_their_own_drives_are_named_for_the_library_and_as_its_downloads()
    {
        var drives = DiskSpaceAndLookupEndpoints.Describe(
            "C:/data",
            [Library("Films", "D:/films", "E:/films-downloads")],
            Read);

        Assert.Equal(["Deluno's own data", "Films", "Films downloads"], drives.Select(drive => drive.Label));
        Assert.Equal(["C:/data", "D:/films", "E:/films-downloads"], drives.Select(drive => drive.Path));
    }

    [Fact]
    public void A_folder_on_a_drive_already_listed_is_not_listed_again_and_the_first_name_wins()
    {
        var drives = DiskSpaceAndLookupEndpoints.Describe(
            "C:/data",
            [Library("Films", "C:/films", "D:/dl"), Library("Shows", "D:/shows", null)],
            Read);

        Assert.Equal(["Deluno's own data", "Films downloads"], drives.Select(drive => drive.Label));
    }

    [Fact]
    public void A_drive_that_cannot_be_read_is_left_out_not_reported_as_zero()
    {
        var drives = DiskSpaceAndLookupEndpoints.Describe(
            "C:/data",
            [Library("Films", "Z:/gone", null)],
            Read);

        Assert.Equal(["Deluno's own data"], drives.Select(drive => drive.Label));
    }

    [Fact]
    public void A_blank_downloads_folder_adds_no_downloads_line()
    {
        var drives = DiskSpaceAndLookupEndpoints.Describe("C:/data", [Library("Films", "D:/films", "  ")], Read);

        Assert.DoesNotContain(drives, drive => drive.Label.EndsWith("downloads", StringComparison.Ordinal));
    }
}
