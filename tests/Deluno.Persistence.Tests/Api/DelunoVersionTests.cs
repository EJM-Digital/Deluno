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
}
