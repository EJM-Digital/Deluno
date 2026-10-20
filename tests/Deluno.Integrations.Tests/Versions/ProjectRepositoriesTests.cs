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

    [Fact]
    public void One_image_name_serves_every_version_and_follows_the_repository()
    {
        Assert.Equal("EJM-Digital/Deluno", ProjectRepositories.Deluno);
        Assert.Equal("ghcr.io/ejm-digital/deluno", ProjectRepositories.DelunoImage);
        Assert.Equal("ghcr.io/" + ProjectRepositories.Deluno.ToLowerInvariant(), ProjectRepositories.DelunoImage);
    }
}
