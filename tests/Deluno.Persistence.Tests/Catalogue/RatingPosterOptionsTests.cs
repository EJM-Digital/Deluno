using System.Text.RegularExpressions;
using Deluno.Contracts;

namespace Deluno.Persistence.Tests.Catalogue;

/// <summary>
/// The poster toggles the server offers are the ones the grid can draw.
///
/// <para>The server generates a toggle id per rating source and serves it; the
/// grid matches on that id to decide whether to print the number. Those are two
/// spellings of one string in two languages, and if they stop agreeing the
/// toggle still appears, still saves, and draws nothing — a control that looks
/// like it is off when it is on.</para>
///
/// <para>There is no type to share across that boundary, so this reads the
/// browser's list the same way <c>ArtworkSizeTests</c> reads the gateway's.
/// Blunt, and the only thing that actually holds.</para>
/// </summary>
public sealed class RatingPosterOptionsTests
{
    [Fact]
    public void Every_rating_toggle_the_server_offers_is_one_the_grid_draws()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "apps", "web", "src", "components", "app", "library-grid.tsx"));

        var drawn = Regex.Matches(source, @"option: ""(?<id>showRating[a-z]+)""")
            .Select(match => match.Groups["id"].Value)
            .ToHashSet(StringComparer.Ordinal);

        var offered = CatalogueControls.For(MediaKind.Movie).PosterOptions
            .Select(option => option.Id)
            .Where(id => id.StartsWith("showRating", StringComparison.Ordinal) && id != "showRating")
            .ToArray();

        Assert.Equal(RatingSources.All.Count, offered.Length);

        foreach (var id in offered)
        {
            Assert.True(drawn.Contains(id), $"The server offers the poster toggle '{id}' and library-grid.tsx draws nothing for it.");
        }
    }

    /// <summary>
    /// And the shelf offers one order per source, which is the half of #319 a
    /// filter cannot stand in for: "the best-reviewed thing I own" is a sort.
    /// </summary>
    [Fact]
    public void Every_source_is_its_own_order()
    {
        var sorts = CatalogueControls.For(MediaKind.Movie).SortFields.Select(sort => sort.Id).ToArray();

        foreach (var source in RatingSources.All)
        {
            Assert.Contains(CatalogueSortFields.ForRating(source.Source), sorts);
        }

        // The blended order stays. It is the right question when you do not care
        // which source said so, and removing it would make the common case the
        // awkward one.
        Assert.Contains(CatalogueSortFields.Rating, sorts);
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
