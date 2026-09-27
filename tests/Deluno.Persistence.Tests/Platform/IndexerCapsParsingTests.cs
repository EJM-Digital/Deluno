using Deluno.Connections;

namespace Deluno.Persistence.Tests.Platform;

/// <summary>
/// Calling a generic Newznab/Torznab indexer's own t=caps endpoint when
/// adding it (#755): its response is parsed into supported categories and
/// search modes, and anything that doesn't look like a caps response
/// yields nothing to pre-fill rather than an error.
/// </summary>
public sealed class IndexerCapsParsingTests
{
    private const string FullCapsResponse =
        """
        <?xml version="1.0" encoding="UTF-8"?>
        <caps>
          <categories>
            <category id="2000" name="Movies">
              <subcat id="2010" name="Foreign"/>
              <subcat id="2040" name="HD"/>
            </category>
            <category id="5000" name="TV">
              <subcat id="5040" name="HD"/>
            </category>
          </categories>
          <searching>
            <search available="yes" supportedParams="q"/>
            <movie-search available="yes" supportedParams="q,imdbid"/>
            <tv-search available="yes" supportedParams="q,season,ep"/>
          </searching>
        </caps>
        """;

    [Fact]
    public void Parses_categories_and_both_search_modes_from_a_full_caps_response()
    {
        var result = ConnectionsEndpointRouteBuilderExtensions.ParseIndexerCaps(FullCapsResponse);

        Assert.NotNull(result);
        Assert.Equal("2000,2010,2040,5000,5040", result.Categories);
        Assert.Equal("both", result.MediaScope);
    }

    [Fact]
    public void Reports_movies_only_when_tv_search_is_not_available()
    {
        var body =
            """
            <caps>
              <categories><category id="2000" name="Movies"/></categories>
              <searching>
                <search available="yes"/>
                <movie-search available="yes"/>
                <tv-search available="no"/>
              </searching>
            </caps>
            """;

        var result = ConnectionsEndpointRouteBuilderExtensions.ParseIndexerCaps(body);

        Assert.NotNull(result);
        Assert.Equal("movies", result.MediaScope);
    }

    [Fact]
    public void Reports_tv_only_when_movie_search_is_not_available()
    {
        var body =
            """
            <caps>
              <categories><category id="5000" name="TV"/></categories>
              <searching>
                <movie-search available="no"/>
                <tv-search available="yes"/>
              </searching>
            </caps>
            """;

        var result = ConnectionsEndpointRouteBuilderExtensions.ParseIndexerCaps(body);

        Assert.NotNull(result);
        Assert.Equal("tv", result.MediaScope);
    }

    [Fact]
    public void Falls_back_to_nothing_when_the_body_has_no_categories()
    {
        var result = ConnectionsEndpointRouteBuilderExtensions.ParseIndexerCaps("<caps><searching><search available=\"yes\"/></searching></caps>");

        Assert.Null(result);
    }

    [Fact]
    public void Falls_back_to_nothing_when_the_body_is_not_xml()
    {
        Assert.Null(ConnectionsEndpointRouteBuilderExtensions.ParseIndexerCaps("this is not xml at all"));
        Assert.Null(ConnectionsEndpointRouteBuilderExtensions.ParseIndexerCaps(""));
    }

    [Fact]
    public void Falls_back_to_nothing_when_the_indexer_returns_a_torznab_error_element_instead_of_caps()
    {
        // FetchIndexerCapsAsync checks for a Torznab/Newznab <error> element
        // (t=caps not implemented, or refused) before ever calling this
        // parser; fed one directly it still finds no categories and yields
        // nothing to pre-fill rather than an error - a caps failure never
        // blocks the add, and the existing t=search-only path still governs
        // whether the indexer itself is healthy.
        var result = ConnectionsEndpointRouteBuilderExtensions.ParseIndexerCaps(
            "<error code=\"203\" description=\"Function Not Available\"/>");

        Assert.Null(result);
    }
}
