using System.Text.RegularExpressions;
using Deluno.Integrations.Metadata;

namespace Deluno.Integrations.Tests.Metadata;

/// <summary>
/// The app and the gateway cache the same artwork at the same size.
///
/// <para>Deluno fetches metadata two ways: straight from TMDb with the user's own
/// key, or through the Cloudflare gateway that fronts a shared one. Both build
/// <c>image.tmdb.org</c> URLs, both feed the same cache, and that cache is keyed
/// by a hash of the URL — so a size changed on one path and not the other does
/// not fail, or warn, or look wrong. It quietly gives a library two resolutions
/// of poster depending on which path imported each title.</para>
///
/// <para>The gateway is JavaScript on the other side of a deploy boundary, so
/// there is no type to share. This test reads its source instead. That is
/// blunter than importing a constant and it is the only thing here that actually
/// holds: a comment asking two files to agree is not a mechanism.</para>
/// </summary>
public sealed class ArtworkSizeTests
{
    [Theory]
    [InlineData("POSTER_SIZE")]
    [InlineData("BACKDROP_SIZE")]
    [InlineData("PORTRAIT_SIZE")]
    public void The_gateway_caches_the_same_size_the_app_does(string constant)
    {
        var expected = constant switch
        {
            "POSTER_SIZE" => ArtworkSizes.Poster,
            "BACKDROP_SIZE" => ArtworkSizes.Backdrop,
            _ => ArtworkSizes.Portrait
        };

        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "services", "metadata-gateway", "src", "index.js"));
        var match = Regex.Match(source, $@"const {constant} = ""(?<size>[^""]+)"";");

        Assert.True(match.Success, $"The gateway no longer declares {constant}. If the constant moved, move this test with it — do not delete it.");
        Assert.Equal(expected, match.Groups["size"].Value);
    }

    /// <summary>
    /// And the sizes are ones TMDb serves. A typo here does not throw; it
    /// returns 404 for every image in the library.
    /// </summary>
    [Fact]
    public void Every_size_is_one_tmdb_actually_offers()
    {
        string[] posters = ["w92", "w154", "w185", "w342", "w500", "w780", "original"];
        string[] backdrops = ["w300", "w780", "w1280", "original"];
        string[] profiles = ["w45", "w185", "h632", "original"];

        Assert.Contains(ArtworkSizes.Poster, posters);
        Assert.Contains(ArtworkSizes.Backdrop, backdrops);
        Assert.Contains(ArtworkSizes.Portrait, profiles);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Deluno.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
