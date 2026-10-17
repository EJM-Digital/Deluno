using Deluno.Api;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The running version keeps its prerelease label, so a release candidate
/// moving to the next one is an update, and its release notes are found.
/// </summary>
public sealed class DelunoVersionTests
{
    [Theory]
    [InlineData("1.0.0-rc.23+66486efc0123", "1.0.0-rc.23")]
    [InlineData("1.0.0-rc.23", "1.0.0-rc.23")]
    [InlineData("1.2.0", "1.2.0")]
    public void The_informational_version_is_used_without_its_commit_suffix(string informational, string expected)
        => Assert.Equal(expected, DelunoVersion.Normalize(informational, new Version(1, 0, 0, 0)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("+abc")]
    public void Without_an_informational_version_the_numeric_version_is_used(string? informational)
        => Assert.Equal("1.0.0", DelunoVersion.Normalize(informational, new Version(1, 0, 0, 0)));

    [Fact]
    public void Two_release_candidates_of_the_same_number_are_different_versions()
        => Assert.NotEqual(
            DelunoVersion.Normalize("1.0.0-rc.22+aaa", new Version(1, 0, 0)),
            DelunoVersion.Normalize("1.0.0-rc.23+bbb", new Version(1, 0, 0)));

    [Fact]
    public void This_build_reports_the_version_set_in_the_build_props()
        => Assert.StartsWith("1.0.0-rc.", DelunoVersion.Of(typeof(DelunoVersion).Assembly));

    [Theory]
    [InlineData("1.0.1", "1.0.0", true)]
    [InlineData("1.0.0", "1.0.1", false)]
    [InlineData("1.1.0", "1.0.9", true)]
    [InlineData("2.0.0", "1.9.9", true)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("v1.0.1", "1.0.0", true)]
    [InlineData("1.0.1+abc", "1.0.0+def", true)]
    // A release is later than its own release candidates, never the other way round.
    [InlineData("1.0.0", "1.0.0-rc.30.33", true)]
    [InlineData("1.0.0-rc.30.33", "1.0.0", false)]
    // Release candidates compare by their numbers, not as text: 30.9 is earlier than 30.10.
    [InlineData("1.0.0-rc.30.34", "1.0.0-rc.30.33", true)]
    [InlineData("1.0.0-rc.30.9", "1.0.0-rc.30.10", false)]
    [InlineData("1.0.0-rc.31", "1.0.0-rc.30.34", true)]
    [InlineData("1.0.0-rc.30", "1.0.0-rc.30.1", false)]
    [InlineData("1.0.0-rc.30.1", "1.0.0-rc.30", true)]
    [InlineData("1.0.0-rc.30.33", "1.0.0-rc.30.33", false)]
    // A numbered label part is earlier than a worded one; worded parts compare as text.
    [InlineData("1.0.0-beta", "1.0.0-1", true)]
    [InlineData("1.0.0-1", "1.0.0-beta", false)]
    [InlineData("1.0.0-rc", "1.0.0-beta", true)]
    [InlineData("1.0.0-beta", "1.0.0-rc", false)]
    public void A_later_release_is_newer_by_semantic_version(string candidate, string running, bool expected)
        => Assert.Equal(expected, DelunoVersion.IsNewer(candidate, running));

    [Theory]
    [InlineData(null, "1.0.0")]
    [InlineData("1.0.0", null)]
    [InlineData("", "1.0.0")]
    [InlineData("   ", "1.0.0")]
    [InlineData("latest", "1.0.0")]
    [InlineData("1.0.0", "latest")]
    [InlineData("1.x.0", "1.0.0")]
    [InlineData("1.2.3.4.5", "1.0.0")]
    public void A_version_that_cannot_be_read_is_never_called_newer(string? candidate, string? running)
        => Assert.False(DelunoVersion.IsNewer(candidate, running));

    [Fact]
    public void A_four_part_version_compares_on_its_first_three_numbers()
        => Assert.True(DelunoVersion.IsNewer("1.0.1.0", "1.0.0.9"));
}
