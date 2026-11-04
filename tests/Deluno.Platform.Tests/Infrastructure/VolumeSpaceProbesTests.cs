using Deluno.Infrastructure.Storage;

namespace Deluno.Platform.Tests.Infrastructure;

public sealed class VolumeSpaceProbesTests
{
    private static VolumeSpace.VolumeProbes System(
        bool windows = false,
        Func<string, string>? fullPath = null,
        Func<string, bool>? exists = null,
        Func<string, VolumeSpace.DriveFacts>? drive = null,
        Func<string, (ulong, ulong)?>? share = null)
        => new(
            windows,
            fullPath ?? (path => path),
            exists ?? (_ => true),
            drive ?? (_ => new VolumeSpace.DriveFacts(1000, 400, "ext4")),
            share ?? (_ => null));

    private static string Slashed(string path) => path.Replace((char)92, '/');

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void No_path_has_no_reading(string? path)
        => Assert.Null(VolumeSpace.TryRead(path, System()));

    [Fact]
    public void Away_from_Windows_a_drive_is_known_by_its_format_and_size()
    {
        var reading = VolumeSpace.TryRead("/films", System());

        Assert.Equal(new VolumeSpaceReading("ext4:1000", 400, 1000), reading);
    }

    [Fact]
    public void On_Windows_a_drive_is_known_by_its_letter()
    {
        var reading = VolumeSpace.TryRead("d:/films", System(windows: true, fullPath: path => "d:/films"));

        Assert.Equal(new VolumeSpaceReading(Path.GetPathRoot("d:/films")!.ToUpperInvariant(), 400, 1000), reading);
    }

    [Fact]
    public void A_folder_not_made_yet_is_read_through_the_nearest_one_that_exists()
    {
        var asked = new List<string>();
        var reading = VolumeSpace.TryRead(
            "/films/new/deeper",
            System(exists: path => Slashed(path) == "/films", drive: path =>
            {
                asked.Add(path);
                return new VolumeSpace.DriveFacts(10, 5, "xfs");
            }));

        Assert.Equal(["/films"], asked.Select(Slashed));
        Assert.Equal("xfs:10", reading!.VolumeKey);
    }

    [Fact]
    public void A_folder_with_nothing_above_it_that_exists_has_no_reading()
        => Assert.Null(VolumeSpace.TryRead("/films/new", System(exists: _ => false)));

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_drive_with_no_size_has_no_reading(long total)
        => Assert.Null(VolumeSpace.TryRead("/films", System(drive: _ => new VolumeSpace.DriveFacts(total, 1, "ext4"))));

    [Theory]
    [InlineData(typeof(ArgumentException))]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(NotSupportedException))]
    public void A_system_that_refuses_to_answer_gives_no_reading(Type failure)
        => Assert.Null(VolumeSpace.TryRead("/films", System(fullPath: _ => throw (Exception)Activator.CreateInstance(failure)!)));

    [Fact]
    public void A_failure_of_another_kind_is_not_hidden()
        => Assert.Throws<InvalidOperationException>(() => VolumeSpace.TryRead("/films", System(fullPath: _ => throw new InvalidOperationException())));

    [Fact]
    public void A_network_share_is_sized_through_the_share_and_named_by_it()
    {
        var reading = VolumeSpace.TryRead(
            "//nas/share/films",
            System(windows: true, share: path => path == "//nas/share/films" ? (400UL, 1000UL) : null));

        Assert.Equal(new VolumeSpaceReading("\\\\NAS\\SHARE", 400, 1000), reading);
    }

    [Fact]
    public void A_share_folder_not_made_yet_is_sized_through_the_nearest_one_that_exists()
    {
        var reading = VolumeSpace.TryRead(
            "//nas/share/films/new",
            System(windows: true, exists: path => Slashed(path) == "//nas/share", share: path => Slashed(path) == "//nas/share" ? (1UL, 2UL) : null));

        Assert.Equal(2, reading!.TotalBytes);
    }

    [Theory]
    [InlineData("//nas/share/films/new", "//nas/share/films")]
    [InlineData("//nas/share/films/", "//nas/share")]
    [InlineData(@"\\nas\share\films", @"\\nas\share")]
    [InlineData("//nas", null)]
    public void A_share_paths_parent_is_read_from_its_text_on_every_system(string path, string? parent)
        => Assert.Equal(parent, VolumeSpace.ShareParentOf(path));

    [Fact]
    public void A_share_with_nothing_that_exists_down_to_the_share_itself_has_no_reading()
        => Assert.Null(VolumeSpace.TryRead("//nas/share/films", System(windows: true, exists: _ => false)));

    [Fact]
    public void A_share_that_will_not_say_or_says_zero_has_no_reading()
    {
        Assert.Null(VolumeSpace.TryRead("//nas/share/films", System(windows: true, share: _ => null)));
        Assert.Null(VolumeSpace.TryRead("//nas/share/films", System(windows: true, share: _ => (5UL, 0UL))));
    }

    [Fact]
    public void Sizes_too_big_for_a_signed_number_are_held_at_the_largest()
    {
        var reading = VolumeSpace.TryRead("//nas/share", System(windows: true, share: _ => (ulong.MaxValue, ulong.MaxValue)));

        Assert.Equal(new VolumeSpaceReading("\\\\NAS\\SHARE", long.MaxValue, long.MaxValue), reading);
    }

    [Fact]
    public void A_share_path_is_only_read_as_one_on_Windows()
    {
        var reading = VolumeSpace.TryRead("//nas/share/films", System(windows: false, exists: _ => true));

        Assert.Equal("ext4:1000", reading!.VolumeKey);
    }

    [Theory]
    [InlineData("\\\\nas\\share\\films", true)]
    [InlineData("//nas/share", true)]
    [InlineData("\\\\?\\C:\\x", false)]
    [InlineData("\\\\.\\C:", false)]
    [InlineData("//", false)]
    [InlineData("/films", false)]
    [InlineData(null, false)]
    public void A_network_share_path_starts_with_two_slashes_and_is_not_a_device_path(string? path, bool expected)
        => Assert.Equal(expected, VolumeSpace.IsNetworkSharePath(path));

    [Theory]
    [InlineData("\\\\nas\\share\\films\\a", "\\\\nas\\share")]
    [InlineData("//nas/share", "\\\\nas\\share")]
    [InlineData("//nas", null)]
    [InlineData("/films", null)]
    public void A_share_root_is_the_server_and_the_share(string path, string? expected)
        => Assert.Equal(expected, VolumeSpace.NetworkShareRoot(path));

    [Fact]
    public void This_system_can_size_a_real_folder()
    {
        Assert.NotNull(VolumeSpace.TryRead(Path.GetTempPath()));
        Assert.NotNull(VolumeSpace.TryRead(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "not-yet")));
        Assert.True(VolumeSpace.VolumeProbes.ThisSystem.DriveOf(Path.GetTempPath()).TotalSize > 0);
    }
}
