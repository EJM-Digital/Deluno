using Deluno.Infrastructure.SystemReadings;

namespace Deluno.Platform.Tests.Infrastructure;

public sealed class WindowsReadingPiecesTests
{
    private static readonly string Backslash = ((char)92).ToString();

    [Fact]
    public void A_file_time_is_its_two_halves_joined()
    {
        Assert.Equal(0x0000000100000002UL, WindowsNativeRules.Combine(1, 2));
        Assert.Equal(ulong.MaxValue, WindowsNativeRules.Combine(uint.MaxValue, uint.MaxValue));
        Assert.Equal(5UL, WindowsNativeRules.Combine(0, 5));
    }

    [Fact]
    public void Busy_time_is_kernel_plus_user()
        => Assert.Equal(new CpuTimes(10, 50), WindowsNativeRules.CpuTimesOf(10, 30, 20));

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData("/", false)]
    [InlineData("C:", true)]
    public void Only_a_drive_root_is_worth_asking_about(string? root, bool expected)
        => Assert.Equal(expected, WindowsNativeRules.IsDriveRoot(root));

    [Fact]
    public void A_drive_root_becomes_a_raw_device_name_without_its_closing_slash()
    {
        var expected = Backslash + Backslash + "." + Backslash + "C:";

        Assert.Equal(expected, WindowsNativeRules.DeviceNameFor("C:" + Backslash));
        Assert.Equal(expected, WindowsNativeRules.DeviceNameFor("C:/"));
        Assert.Equal(expected, WindowsNativeRules.DeviceNameFor("C:"));
    }

    [Fact]
    public void Neither_nothing_nor_the_invalid_handle_is_an_open_device()
    {
        Assert.False(WindowsNativeRules.IsOpened(IntPtr.Zero));
        Assert.False(WindowsNativeRules.IsOpened(new IntPtr(-1)));
        Assert.True(WindowsNativeRules.IsOpened(new IntPtr(7)));
    }

    [Fact]
    public void Disk_counters_add_reads_and_writes_into_operations_without_overflowing()
        => Assert.Equal(
            new DiskCounters(1, 2, uint.MaxValue + (long)uint.MaxValue, 4, 5),
            WindowsNativeRules.CountersOf(1, 2, uint.MaxValue, uint.MaxValue, 4, 5));

    [Fact]
    public void The_operating_system_name_loses_the_makers_name()
        => Assert.Equal("Windows 11 Pro", WindowsReadingRules.PlainCaption("Microsoft Windows 11 Pro"));

    [Fact]
    public void A_volume_key_is_its_root_without_the_closing_slash_in_capitals()
    {
        Assert.Equal("D:", WindowsReadingRules.Key("d:" + Backslash));
        Assert.Equal("D:", WindowsReadingRules.Key("d:/"));
    }

    [Fact]
    public void The_machines_disk_is_the_volumes_added_together_or_unknown_when_there_are_none()
    {
        var one = new DiskCounters(1, 2, 3, 4, 5);
        var two = new DiskCounters(10, 20, 30, 40, 50);

        var both = WindowsReadingRules.SnapshotOf(new Dictionary<string, DiskCounters> { ["C:"] = one, ["D:"] = two });
        var none = WindowsReadingRules.SnapshotOf(new Dictionary<string, DiskCounters>());

        Assert.Equal(new DiskCounters(11, 22, 33, 44, 55), both.Machine);
        Assert.Equal(2, both.Volumes.Count);
        Assert.Null(none.Machine);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("//nas/share/films", null)]
    [InlineData("D:/films", "D:")]
    [InlineData("noroot", null)]
    [InlineData("rootless", "")]
    public void A_folders_volume_key_is_its_drive_and_nothing_for_a_share_or_a_path_with_no_drive(string? path, string? expected)
    {
        var key = WindowsReadingRules.VolumeKey(path!, folder => folder == "rootless" ? "" : folder.StartsWith("D:", StringComparison.Ordinal) ? "D:/" : "/");

        Assert.Equal(expected is "" ? null : expected, key);
    }

    [Theory]
    [InlineData(typeof(ArgumentException))]
    [InlineData(typeof(NotSupportedException))]
    [InlineData(typeof(PathTooLongException))]
    public void A_path_that_cannot_be_read_has_no_volume_key(Type failure)
        => Assert.Null(WindowsReadingRules.VolumeKey("D:/x", _ => throw (Exception)Activator.CreateInstance(failure)!));

    [Fact]
    public void A_failure_of_another_kind_is_not_hidden()
        => Assert.Throws<FormatException>(() => WindowsReadingRules.VolumeKey("D:/x", _ => throw new FormatException()));

    [Fact]
    public void A_disk_is_found_by_its_number_written_the_same_way()
    {
        Assert.True(WindowsReadingRules.IsDisk("2", 2));
        Assert.False(WindowsReadingRules.IsDisk("3", 2));
        Assert.False(WindowsReadingRules.IsDisk(null, 2));
        Assert.False(WindowsReadingRules.IsDisk("02", 2));
    }

    [Fact]
    public void Drive_letters_match_whatever_their_case()
    {
        Assert.True(WindowsReadingRules.SameLetter('c', 'C'));
        Assert.False(WindowsReadingRules.SameLetter('c', 'D'));
    }

    [Fact]
    public void A_health_code_is_a_number_or_minus_one_when_absent()
    {
        Assert.Equal(1, WindowsReadingRules.HealthCode((ushort)1));
        Assert.Equal(2, WindowsReadingRules.HealthCode("2"));
        Assert.Equal(-1, WindowsReadingRules.HealthCode(null));
    }

    [Fact]
    public void Operational_codes_are_the_list_or_empty_when_it_is_anything_else()
    {
        Assert.Equal([3, 5], WindowsReadingRules.OperationalCodes(new ushort[] { 3, 5 }));
        Assert.Empty(WindowsReadingRules.OperationalCodes("3"));
        Assert.Empty(WindowsReadingRules.OperationalCodes(null));
    }

    [Fact]
    public void A_registry_value_counts_only_when_it_holds_something()
    {
        Assert.True(WindowsReadingRules.HasContent(new[] { "a" }));
        Assert.True(WindowsReadingRules.HasContent("a"));
        Assert.False(WindowsReadingRules.HasContent(Array.Empty<string>()));
        Assert.False(WindowsReadingRules.HasContent(""));
        Assert.False(WindowsReadingRules.HasContent(5));
        Assert.False(WindowsReadingRules.HasContent(null));
    }

    [Theory]
    [InlineData(3, "The drive is running in a reduced state.")]
    [InlineData(5, "The drive expects to fail soon.")]
    [InlineData(6, "The drive has an error.")]
    [InlineData(10, "The drive has stopped.")]
    [InlineData(12, "Windows lost contact with the drive.")]
    [InlineData(13, "The drive is out of space to repair itself.")]
    public void Each_status_a_drive_can_report_is_put_in_plain_words(int code, string words)
        => Assert.Equal(words, DriveHealthRules.Map(1, [code]).Note);

    [Fact]
    public void A_failing_drive_with_no_known_status_still_says_it_is_failing()
    {
        Assert.Equal("The drive reports that it is failing.", DriveHealthRules.Map(2, [99]).Note);
        Assert.Equal(DriveHealthReading.Unknown, DriveHealthRules.Map(7, []));
    }

    [Fact]
    public void A_drive_that_expects_to_fail_says_so()
        => Assert.Equal(new DriveHealthReading("failing", "The drive expects to fail soon."), WindowsReadingRules.PredictedToFail);
}
