using Deluno.Contracts;

namespace Deluno.Integrations.Tests.Versions;

/// <summary>#1117: when a connection's version is out of date, and when Deluno stays quiet.</summary>
public sealed class ReleaseVersionsTests
{
    [Theory]
    [InlineData("4.6.7", "5.0.2")]
    [InlineData("v4.6.7", "v5.0.2")]
    [InlineData("4.6.7-ls123", "5.0.2")]
    [InlineData("4.6.7+build.9", "4.6.8")]
    [InlineData("1.43.3.10540-abcdef012", "1.43.4.10903")]
    [InlineData("4.6", "4.6.1")]
    [InlineData("4.9.0", "4.10.0")]
    [InlineData("2.0.4", "2.2.0")]
    public void An_older_stable_build_is_out_of_date(string running, string latest)
        => Assert.True(ReleaseVersions.IsOutOfDate(running, latest));

    [Theory]
    [InlineData("5.0.2", "5.0.2")]
    [InlineData("v5.0.2", "5.0.2")]
    [InlineData("5.0.2-ls150", "5.0.2")]
    [InlineData("4.6", "4.6.0")]
    [InlineData("5.1.0", "5.0.2")]
    [InlineData("10.0.0", "9.9.9")]
    public void An_equal_or_newer_build_is_up_to_date(string running, string latest)
        => Assert.False(ReleaseVersions.IsOutOfDate(running, latest));

    [Theory]
    [InlineData("5.0.0-beta1", "5.0.2")]
    [InlineData("5.0.0beta1", "5.0.2")]
    [InlineData("5.0.0rc2", "5.0.2")]
    [InlineData("5.0.0-RC.2", "5.0.0")]
    [InlineData("2.0.0b1", "2.2.0")]
    [InlineData("2.1.1.dev0", "2.2.0")]
    [InlineData("0.0.1-dev", "4.0.0")]
    [InlineData("4.1.0-nightly", "4.1.1")]
    [InlineData("1.0.0-rc.1", "1.0.0-rc.2")]
    [InlineData("1.0.0-rc.9", "1.0.0")]
    [InlineData("1.0.0-rc.2", "1.0.0-rc.10")]
    [InlineData("1.0.0-rc.1", "1.0.1-rc.1")]
    public void A_pre_release_build_is_out_of_date_when_any_newer_release_exists(string running, string latest)
        => Assert.True(ReleaseVersions.IsOutOfDate(running, latest));

    [Theory]
    [InlineData("1.0.0-rc.2", "1.0.0-rc.2")]
    [InlineData("1.0.0-rc.2", "1.0.0-rc.1")]
    [InlineData("1.0.0-rc.1", "0.9.0")]
    [InlineData("1.0.0", "1.0.0-rc.9")]
    [InlineData("5.0.0-beta1", "5.0.0-beta1")]
    public void A_pre_release_build_that_is_current_or_ahead_is_up_to_date(string running, string latest)
        => Assert.False(ReleaseVersions.IsOutOfDate(running, latest));

    [Theory]
    [InlineData("5.0.2", "5.1.0-beta1")]
    [InlineData("5.0.2", "6.0.0rc1")]
    [InlineData("1.0.0", "1.0.1-rc.1")]
    public void A_stable_build_is_compared_only_with_stable_releases(string running, string latest)
        => Assert.False(ReleaseVersions.IsOutOfDate(running, latest));

    [Fact]
    public void Release_order_puts_rc2_over_rc1_and_stable_over_any_rc()
    {
        int Order(string a, string b) => Math.Sign(ReleaseVersions.ComparePrecedence(ReleaseVersions.Parse(a)!, ReleaseVersions.Parse(b)!));

        Assert.Equal(1, Order("1.0.0-rc.2", "1.0.0-rc.1"));
        Assert.Equal(1, Order("1.0.0-rc.10", "1.0.0-rc.9"));
        Assert.Equal(1, Order("1.0.0", "1.0.0-rc.9"));
        Assert.Equal(1, Order("1.0.0-rc.1", "1.0.0-beta.5"));
        Assert.Equal(0, Order("v1.0.0-rc.1", "1.0.0-rc1"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("unknown")]
    [InlineData("latest")]
    public void A_version_that_cannot_be_read_is_never_called_out_of_date(string? running)
    {
        Assert.False(ReleaseVersions.IsOutOfDate(running, "5.0.2"));
        Assert.False(ReleaseVersions.IsOutOfDate("4.0.0", running));
    }

    [Fact]
    public void A_hex_build_hash_is_not_mistaken_for_a_beta()
    {
        // Plex writes its version as number-hash; "b5521bd8c" starts with a letter a beta tag also could.
        Assert.False(ReleaseVersions.Parse("1.43.4.10903-b5521bd8c")!.IsPreRelease);
        Assert.True(ReleaseVersions.IsOutOfDate("1.43.4.10903-b5521bd8c", "1.43.5.10999"));
    }

    [Fact]
    public void Parsing_reads_the_numbers_and_the_pre_release_flag()
    {
        var parsed = ReleaseVersions.Parse("v5.3.0rc1")!;
        Assert.Equal([5, 3, 0], parsed.Parts);
        Assert.True(parsed.IsPreRelease);
        Assert.Null(ReleaseVersions.Parse("release-5.2.4"));
        Assert.Null(ReleaseVersions.Parse("99999999999.1"));
    }
}
