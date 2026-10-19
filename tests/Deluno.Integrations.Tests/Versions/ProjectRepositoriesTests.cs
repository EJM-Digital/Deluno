using Deluno.Companions;
using Deluno.Contracts;
using Xunit;

namespace Deluno.Integrations.Tests.Versions;

public sealed class ProjectRepositoriesTests
{
    [Fact]
    public void Weir_has_moved_and_every_address_is_built_from_the_two_values()
    {
        Assert.Equal("EJM-Digital/Weir", ProjectRepositories.Weir);
        Assert.Equal("https://github.com/EJM-Digital/Weir", ProjectRepositories.WeirWebUrl);
        Assert.Equal($"https://github.com/{ProjectRepositories.Deluno}", ProjectRepositories.DelunoWebUrl);
        Assert.Equal($"Deluno/1.0 (+{ProjectRepositories.DelunoWebUrl})", ProjectRepositories.DelunoUserAgent);
        Assert.Equal(ProjectRepositories.DelunoUserAgent, IndexerApiEndpoint.UserAgent);
        Assert.Equal(ProjectRepositories.Weir, CompanionCatalog.WeirRepository);
    }

    [Theory]
    [InlineData("https://github.com/jampat000/Deluno")]
    [InlineData("HTTPS://GITHUB.COM/JAMPAT000/DELUNO/")]
    [InlineData("  https://github.com/jampat000/Deluno.git ")]
    public void A_former_default_update_address_is_recognised(string saved)
        => Assert.True(ProjectRepositories.IsFormerDelunoDefault(saved));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://example.invalid/Deluno")]
    [InlineData("https://github.com/jampat000/Deluno-fork")]
    public void Anything_else_is_not(string? saved)
        => Assert.False(ProjectRepositories.IsFormerDelunoDefault(saved));

    [Theory]
    [InlineData("1.0.0-rc.3")]
    [InlineData("v1.0.0-rc.30.21")]
    [InlineData("1.0.0-rc.30.34")]
    public void Releases_up_to_rc_30_34_are_at_the_former_image_forever(string version)
        => Assert.Equal("ghcr.io/jampat000/deluno", ProjectRepositories.DelunoImageForVersion(version));

    [Theory]
    [InlineData("1.0.0-rc.30.35")]
    [InlineData("1.0.0-rc.31")]
    [InlineData("1.0.0")]
    public void Later_releases_are_at_the_current_repositorys_lower_cased_image(string version)
    {
        Assert.Equal(ProjectRepositories.DelunoImage, ProjectRepositories.DelunoImageForVersion(version));
        Assert.Equal("ghcr.io/" + ProjectRepositories.Deluno.ToLowerInvariant(), ProjectRepositories.DelunoImage);
    }

    [Fact]
    public void Versions_order_numerically_and_a_release_is_newer_than_its_candidates()
    {
        Assert.True(ProjectRepositories.CompareVersions("1.0.0-rc.9", "1.0.0-rc.10") < 0);
        Assert.True(ProjectRepositories.CompareVersions("1.0.0", "1.0.0-rc.30.34") > 0);
        Assert.Equal(0, ProjectRepositories.CompareVersions("v1.0.0-rc.30.34", "1.0.0-rc.30.34"));
        Assert.True(ProjectRepositories.CompareVersions("1.0.0-rc.30.35", ProjectRepositories.LastFormerImageVersion) > 0);
    }

}
