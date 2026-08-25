using Deluno.Worker.Services;

namespace Deluno.Worker.Tests.Services;

public sealed class ReleaseNameParserTests
{
    [Theory]
    // The defect that prompted this: the title's own number came first, so the
    // import was filed under "(2049)" instead of the 2017 release year (#268).
    [InlineData("Blade.Runner.2049.2017.1080p.WEB-DL.DD5.1.H.264-GROUP", 2017)]
    [InlineData("2001.A.Space.Odyssey.1968.2160p.UHD.BluRay-GROUP", 1968)]
    [InlineData("Arrival.2016.1080p.BluRay.x264-SPARKS", 2016)]
    [InlineData("Blade Runner 2049 (2017) [1080p]", 2017)]
    public void InferYear_takes_the_release_year_not_a_year_like_title_token(string releaseName, int expected)
        => Assert.Equal(expected, ReleaseNameParser.InferYear(releaseName));

    [Theory]
    [InlineData("Some.Show.S01E01.1080p.WEB-DL-GROUP")]
    [InlineData("")]
    [InlineData("   ")]
    public void InferYear_returns_null_when_no_year_is_present(string releaseName)
        => Assert.Null(ReleaseNameParser.InferYear(releaseName));

    [Fact]
    public void InferYear_ignores_numbers_outside_the_plausible_range()
        => Assert.Null(ReleaseNameParser.InferYear("Release.1234.5678.x264-GROUP"));

    [Fact]
    public void InferYear_keeps_a_lone_year_like_title_token_when_it_is_all_there_is()
    {
        // Nothing better is available, so the fallback still answers; an import
        // whose grab is known takes the catalogue year and never reaches here.
        Assert.Equal(2049, ReleaseNameParser.InferYear("Blade.Runner.2049.1080p.WEB-DL-GROUP"));
    }
}
