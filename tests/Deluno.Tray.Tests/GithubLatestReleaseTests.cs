using Deluno.Tray;
using Xunit;

namespace Deluno.Tray.Tests;

public sealed class GithubLatestReleaseTests
{
    [Fact]
    public void Reads_the_matching_line_from_a_standard_SHA256SUMS_file()
    {
        var sums = "abc123  RELEASES\n" +
                   "d34db33f00000000000000000000000000000000000000000000000000000000  Weir-win-Setup.exe\n" +
                   "cafef00d  weir-web-dist.zip\n";

        var hash = GithubLatestRelease.ParseChecksum(sums, "Weir-win-Setup.exe");

        Assert.Equal("d34db33f00000000000000000000000000000000000000000000000000000000", hash);
    }

    [Fact]
    public void Answers_null_when_the_file_has_no_matching_line()
    {
        var hash = GithubLatestRelease.ParseChecksum("abc123  something-else.exe\n", "Weir-win-Setup.exe");

        Assert.Null(hash);
    }
}
