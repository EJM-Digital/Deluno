using System.Globalization;
using Deluno.Contracts;

namespace Deluno.Platform.Tests.Contracts;

/// <summary>CI, 28 Oct: "P0" wrote "50 %" under the invariant culture a Linux host and the Docker image run with.</summary>
public sealed class PlainPercentTests
{
    [Theory]
    [InlineData("")]
    [InlineData("en-US")]
    [InlineData("fr-FR")]
    [InlineData("de-DE")]
    public void A_share_reads_the_same_in_every_culture(string culture)
    {
        var before = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = culture.Length == 0 ? CultureInfo.InvariantCulture : new CultureInfo(culture);
        try
        {
            Assert.Equal("50%", PlainPercent.Of(0.5));
            Assert.Equal("0%", PlainPercent.Of(0));
            Assert.Equal("100%", PlainPercent.Of(1));
            Assert.Equal("12.5%", PlainPercent.Of(0.125, 1));
            Assert.Equal("33%", PlainPercent.Of(1d / 3));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }
}
