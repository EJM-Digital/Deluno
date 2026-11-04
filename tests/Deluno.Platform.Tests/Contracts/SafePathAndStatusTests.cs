using Deluno.Contracts;

namespace Deluno.Platform.Tests.Contracts;

public sealed class SafePathAndStatusTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("a/b", "a/b")]
    [InlineData(" a\\b\\c ", "a/b/c")]
    [InlineData("/a/./b//c/", "/a/b/c")]
    [InlineData("/a/b/../c", "/a/c")]
    [InlineData("/", "/")]
    [InlineData("a/..", "")]
    [InlineData("../a", null)]
    [InlineData("/..", null)]
    [InlineData("/a/../..", null)]
    [InlineData("D:/films/../tv", "D:/tv")]
    [InlineData("D:/..", null)]
    [InlineData("D:\\films\\..\\..\\x", null)]
    [InlineData("//nas/share/films/..", "//nas/share")]
    [InlineData("//nas/share/..", null)]
    [InlineData("//nas/..", null)]
    public void A_path_is_tidied_and_one_that_climbs_out_of_its_root_is_refused(string? path, string? expected)
        => Assert.Equal(expected, SafePath.Normalize(path));

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData("a/b", false)]
    [InlineData("a/./b", true)]
    [InlineData("a\\..\\b", true)]
    [InlineData("a/ .. /b", true)]
    [InlineData("a/.../b", false)]
    public void Dot_segments_are_found_in_either_kind_of_slash(string? path, bool expected)
        => Assert.Equal(expected, SafePath.HasDotSegments(path));

    [Theory]
    [InlineData(null, null)]
    [InlineData(" ", null)]
    [InlineData("/a/../b", null)]
    [InlineData("  /a/b  ", "/a/b")]
    public void A_path_a_program_reported_is_kept_only_when_it_has_no_dot_segments(string? path, string? expected)
        => Assert.Equal(expected, SafePath.ReportedPath(path));

    [Theory]
    [InlineData(null, false)]
    [InlineData(" ", false)]
    [InlineData("/a/b", true)]
    [InlineData("D:\\a\\b", true)]
    [InlineData("a/b", false)]
    [InlineData("a", false)]
    [InlineData("/a/../b", false)]
    [InlineData("D:/a/./b", false)]
    public void A_plain_full_path_is_rooted_and_has_no_dot_segments(string? path, bool expected)
        => Assert.Equal(expected, SafePath.IsPlainFullPath(path));

    [Theory]
    [InlineData("/films", "/films/a", false, true)]
    [InlineData("/films/", "/films/a/", false, true)]
    [InlineData("/films", "/films", false, false)]
    [InlineData("/films", "/films", true, true)]
    [InlineData("/films", "/films-old/a", false, false)]
    [InlineData("/films", "/other", false, false)]
    [InlineData(null, "/films", false, false)]
    [InlineData("/films", null, false, false)]
    [InlineData("/films", "/films/../x", false, false)]
    public void A_path_is_inside_a_root_only_below_it(string? root, string? path, bool allowEqual, bool expected)
        => Assert.Equal(expected, SafePath.IsInside(root, path, allowEqual, StringComparison.Ordinal));

    [Fact]
    public void Letter_case_counts_the_way_this_system_counts_it_unless_told_otherwise()
    {
        Assert.True(SafePath.IsInside("/Films", "/films/a", comparison: StringComparison.OrdinalIgnoreCase));
        Assert.False(SafePath.IsInside("/Films", "/films/a", comparison: StringComparison.Ordinal));
        Assert.Equal(OperatingSystem.IsWindows(), SafePath.IsInside("/Films", "/films/a"));
        Assert.Equal(!OperatingSystem.IsWindows(), !SafePath.IsInside("/Films", "/films/a"));
    }

    [Fact]
    public void A_relative_path_is_put_under_its_root()
    {
        Assert.True(SafePath.TryResolveUnder("/films/", "Film (2020)\\Film.mkv", out var result, out var reason));
        Assert.Equal("/films/Film (2020)/Film.mkv", result);
        Assert.Equal(string.Empty, reason);
    }

    [Theory]
    [InlineData("/films", null)]
    [InlineData("/films", "  ")]
    public void Nothing_to_put_under_the_root_gives_the_root(string root, string? relative)
    {
        Assert.True(SafePath.TryResolveUnder(root, relative, out var result, out _));
        Assert.Equal("/films", result);
    }

    [Fact]
    public void No_root_gives_a_reason()
    {
        Assert.False(SafePath.TryResolveUnder(null, "a", out var result, out var reason));
        Assert.Equal(string.Empty, result);
        Assert.Equal("There is no folder to put that path under.", reason);
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("\\etc")]
    [InlineData("C:\\Windows")]
    [InlineData("../x")]
    [InlineData("a/../../x")]
    public void A_path_that_is_rooted_or_climbs_out_is_refused_with_the_reason_given(string relative)
    {
        Assert.False(SafePath.TryResolveUnder("/films", relative, out var result, out var reason));
        Assert.Equal(string.Empty, result);
        Assert.Equal(SafePath.OutsideRootReason, reason);
    }

    [Theory]
    [InlineData("sent", "succeeded")]
    [InlineData("failed", "failed")]
    [InlineData("planned", "planned")]
    [InlineData("circuitOpen", "circuit_open")]
    [InlineData("notFound", "not_found")]
    [InlineData("paused", "paused")]
    [InlineData("unready", "unready")]
    [InlineData("unavailable", "unavailable")]
    [InlineData("anything else", "anything else")]
    public void A_grab_answer_is_stored_under_its_stored_name(string status, string stored)
        => Assert.Equal(stored, GrabStatuses.ToStored(status));

    [Theory]
    [InlineData("not_found", true)]
    [InlineData("paused", true)]
    [InlineData("planned", true)]
    [InlineData("circuit_open", true)]
    [InlineData("succeeded", false)]
    [InlineData("failed", false)]
    [InlineData(null, false)]
    public void Only_a_stored_grab_that_is_waiting_counts_as_waiting(string? stored, bool expected)
        => Assert.Equal(expected, GrabStatuses.IsStoredWaiting(stored));

    [Fact]
    public void A_release_service_with_nothing_to_add_has_no_limit_and_no_problem()
    {
        ILatestReleaseService service = new PlainReleaseService();

        Assert.Null(service.LimitResetsUtc("deluno"));
        Assert.Null(service.CheckProblem("deluno"));
        Assert.Equal("1.0", service.Peek("deluno")!.Version);
    }

    [Fact]
    public void A_connection_version_is_what_was_read_and_what_is_latest()
    {
        var read = new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);
        var row = new ConnectionVersionRow("downloadClient", "c1", "qbittorrent", "5.0", read);
        var item = new ConnectionVersionItem("downloadClient", "c1", "5.0", "5.1", true, "https://example.test/r", read, "rate limited");

        Assert.Null(row.DismissedVersion);
        Assert.Equal(row with { DismissedVersion = "5.1" }, row with { DismissedVersion = "5.1" });
        Assert.NotEqual(row, row with { Version = "5.2" });
        Assert.Equal(("5.1", true, read, "rate limited"), (item.LatestVersion, item.UpdateAvailable, item.LatestCheckPausedUntilUtc, item.LatestCheckProblem));
        Assert.Equal(new LatestRelease("1", null), new LatestRelease("1", null));
    }

    private sealed class PlainReleaseService : ILatestReleaseService
    {
        public LatestRelease? Peek(string project) => new("1.0", null);
    }
}
