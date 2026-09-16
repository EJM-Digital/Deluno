using Deluno.Integrations.Metadata;

namespace Deluno.Integrations.Tests.Metadata;

/// <summary>
/// #509: a year typed after a title is how people say which film they mean,
/// and a number that belongs to the title has to be left where it is.
/// </summary>
public sealed class MetadataQueryYearTests
{
    private const int CurrentYear = 2026;

    [Theory]
    [InlineData("Night of the Living Dead 1968", "Night of the Living Dead", 1968)]
    [InlineData("Night of the Living Dead (1968)", "Night of the Living Dead", 1968)]
    [InlineData("Nosferatu(1922)", "Nosferatu", 1922)]
    [InlineData("  Metropolis [1927]  ", "Metropolis", 1927)]
    [InlineData("The Office - 2005", "The Office", 2005)]
    [InlineData("Wonder Woman 1984", "Wonder Woman", 1984)]
    public void A_trailing_release_year_is_split_from_the_title(string query, string title, int year)
    {
        Assert.True(MetadataQueryYear.TrySplit(query, CurrentYear, out var actualTitle, out var actualYear));
        Assert.Equal(title, actualTitle);
        Assert.Equal(year, actualYear);
    }

    [Theory]
    [InlineData("1917")]
    [InlineData("(1917)")]
    [InlineData("Blade Runner 2049")]
    [InlineData("2001: A Space Odyssey")]
    [InlineData("Apollo 13")]
    [InlineData("Ocean's 11")]
    [InlineData("The Thing")]
    [InlineData("")]
    public void A_number_that_is_the_title_or_part_of_it_is_left_alone(string query)
    {
        Assert.False(MetadataQueryYear.TrySplit(query, CurrentYear, out _, out _));
    }
}
