using Deluno.Contracts;

namespace Deluno.Platform.Tests.Contracts;

public sealed class HostAndPathHelpersTests
{
    [Fact]
    public void The_machine_name_is_used_when_it_can_be_read()
    {
        Assert.Equal("nas-one", DelunoHostName.NameFrom(() => "nas-one"));
        Assert.False(string.IsNullOrWhiteSpace(DelunoHostName.Current));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void A_machine_with_no_name_is_called_Deluno(string? name)
        => Assert.Equal("Deluno", DelunoHostName.NameFrom(() => name));

    [Fact]
    public void A_machine_name_that_cannot_be_read_is_called_Deluno()
        => Assert.Equal("Deluno", DelunoHostName.NameFrom(() => throw new InvalidOperationException("no name")));

    [Theory]
    [InlineData("0123456789ab", true, true)]
    [InlineData("0123456789ab", false, false)]
    [InlineData(null, true, false)]
    [InlineData("0123456789a", true, false)]
    [InlineData("0123456789abc", true, false)]
    [InlineData("0123456789AB", true, false)]
    [InlineData("0123456789ag", true, false)]
    [InlineData("0123456789a/", true, false)]
    [InlineData("0123456789a`", true, false)]
    [InlineData("0123456789a:", true, false)]
    [InlineData("fedcba987650", true, true)]
    public void Only_a_twelve_character_lowercase_hex_name_inside_a_container_is_a_container_id(string? name, bool inContainer, bool expected)
        => Assert.Equal(expected, DelunoHostName.LooksLikeContainerId(name, inContainer));

    [Fact]
    public void This_machine_is_asked_whether_its_name_is_a_container_id()
    {
        var original = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER");
        try
        {
            Environment.SetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER", "false");
            Assert.False(DelunoHostName.CurrentLooksLikeContainerId);
            Environment.SetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER", "TRUE");
            Assert.Equal(DelunoHostName.LooksLikeContainerId(DelunoHostName.Current, true), DelunoHostName.CurrentLooksLikeContainerId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER", original);
        }
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("Film.mkv", "Film.mkv")]
    [InlineData("/films/Film (2020)/Film.mkv", "Film.mkv")]
    [InlineData("D:\\films\\Film.mkv", "Film.mkv")]
    [InlineData("/films/Film/", "")]
    public void The_file_name_is_what_follows_the_last_slash_of_either_kind(string? path, string expected)
        => Assert.Equal(expected, MediaPath.FileName(path));

    [Theory]
    [InlineData(null, "")]
    [InlineData("/films/Film.2020.mkv", "Film.2020")]
    [InlineData("/films/Film", "Film")]
    [InlineData("/films/.hidden", ".hidden")]
    public void The_name_without_extension_drops_only_the_last_dot_part(string? path, string expected)
        => Assert.Equal(expected, MediaPath.FileNameWithoutExtension(path));

    [Fact]
    public void A_mount_table_gives_the_second_column_with_octal_escapes_decoded()
    {
        var roots = MountedVolumes.ParseMountTable(["/dev/sda1 / ext4 rw 0 0", "nas:/share /mnt/my\\040films nfs rw 0 0", "bad"]);

        Assert.Equal(["/", "/mnt/my films"], roots);
    }

    [Fact]
    public void On_Linux_the_roots_come_from_the_mount_table()
        => Assert.Equal(["/", "/data"], MountedVolumes.Roots(true, () => ["x / a", "x /data a"], () => throw new InvalidOperationException()));

    [Fact]
    public void Elsewhere_the_roots_are_the_drives()
        => Assert.Equal(["C:\\", "D:\\"], MountedVolumes.Roots(false, () => throw new InvalidOperationException(), () => ["C:\\", "D:\\"]));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_system_that_will_not_say_what_is_mounted_has_no_roots(bool isLinux)
    {
        Assert.Empty(MountedVolumes.Roots(isLinux, () => throw new IOException(), () => throw new IOException()));
        Assert.Empty(MountedVolumes.Roots(isLinux, () => throw new UnauthorizedAccessException(), () => throw new UnauthorizedAccessException()));
    }

    [Fact]
    public void The_drives_of_this_system_can_be_listed()
        => Assert.NotEmpty(MountedVolumes.DriveNames());

    [Fact]
    public void The_mount_table_of_this_system_is_read_where_there_is_one()
    {
        if (OperatingSystem.IsLinux())
        {
            Assert.NotEmpty(MountedVolumes.ReadLinuxMountTable());
        }
    }

    [Fact]
    public void The_roots_of_this_system_can_be_listed()
        => Assert.NotNull(MountedVolumes.Roots());

    [Fact]
    public void A_path_belongs_to_the_longest_root_that_contains_it()
    {
        string[] roots = ["/", "/mnt/films", "/mnt/films-old", ""];

        Assert.Equal("/mnt/films", MountedVolumes.RootOf("/mnt/films/a.mkv", roots, StringComparison.Ordinal));
        Assert.Equal("/mnt/films", MountedVolumes.RootOf("/mnt/films", roots, StringComparison.Ordinal));
        Assert.Equal("/mnt/films-old", MountedVolumes.RootOf("/mnt/films-old/a.mkv", roots, StringComparison.Ordinal));
        Assert.Equal("/", MountedVolumes.RootOf("/mnt/filmsX/a.mkv", roots, StringComparison.Ordinal));
        Assert.Equal("/", MountedVolumes.RootOf("/other", roots, StringComparison.Ordinal));
        Assert.Null(MountedVolumes.RootOf("relative", ["/x"], StringComparison.Ordinal));
    }

    [Fact]
    public void A_root_that_ends_in_a_slash_contains_what_follows_it_and_a_backslash_counts_as_a_slash()
    {
        Assert.Equal("D:\\", MountedVolumes.RootOf("D:\\films\\a.mkv", ["D:\\"], StringComparison.Ordinal));
        Assert.Equal("D:", MountedVolumes.RootOf("D:\\films", ["D:"], StringComparison.Ordinal));
        Assert.Null(MountedVolumes.RootOf("D:films", ["D:"], StringComparison.Ordinal));
    }

    [Fact]
    public void Letter_case_matters_only_on_systems_where_it_does()
    {
        Assert.Equal("C:\\", MountedVolumes.RootOf("c:\\a", ["C:\\"], StringComparison.OrdinalIgnoreCase));
        Assert.Null(MountedVolumes.RootOf("c:\\a", ["C:\\"], StringComparison.Ordinal));
        Assert.Equal(StringComparison.OrdinalIgnoreCase, MountedVolumes.PathComparisonFor(true));
        Assert.Equal(StringComparison.Ordinal, MountedVolumes.PathComparisonFor(false));
        Assert.Equal(MountedVolumes.PathComparisonFor(OperatingSystem.IsWindows()), MountedVolumes.PathComparison);
        Assert.Equal("/data", MountedVolumes.RootOf("/data/x", ["/data"]));
    }
}
