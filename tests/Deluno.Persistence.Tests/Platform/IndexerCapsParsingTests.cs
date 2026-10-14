using Deluno.Connections;
using Deluno.Tests.Recorded;

namespace Deluno.Persistence.Tests.Platform;

/// <summary>
/// Calling a generic Newznab/Torznab indexer's own t=caps endpoint when
/// adding it (#755): its response is parsed into supported categories and
/// search modes, and anything that doesn't look like a caps response
/// yields nothing to pre-fill rather than an error.
///
/// The caps documents are the real ones (#1237): Jackett's Torznab caps for a
/// movies-only tracker and for the aggregate "all" indexer, and NZBHydra2's
/// Newznab caps. A tv-only indexer has not been recorded, so that test is the
/// real movies-only document with the one value it is about turned round.
/// </summary>
public sealed class IndexerCapsParsingTests
{
    [Fact]
    public void Parses_categories_and_both_search_modes_from_a_full_caps_response()
    {
        var result = ConnectionsEndpointRouteBuilderExtensions.ParseIndexerCaps(RecordedReplies.Jackett("caps-all").Single.Reply.Body!);

        Assert.NotNull(result);
        Assert.Contains("2000", result!.Categories!.Split(','));
        Assert.Contains("5000", result!.Categories!.Split(','));
        Assert.Contains("2040", result!.Categories!.Split(','));
        Assert.Equal("both", result.MediaScope);
    }

    [Fact]
    public void Parses_a_newznab_servers_caps_the_same_way()
    {
        var result = ConnectionsEndpointRouteBuilderExtensions.ParseIndexerCaps(RecordedReplies.Nzbhydra2("caps").Single.Reply.Body!);

        Assert.NotNull(result);
        Assert.Contains("2040", result!.Categories!.Split(','));
        Assert.Contains("5040", result!.Categories!.Split(','));
        Assert.Equal("both", result.MediaScope);
    }

    [Fact]
    public void Reports_movies_only_when_tv_search_is_not_available()
    {
        // The real caps of a movies-only tracker (YTS): <tv-search available="no" ...>.
        var body = RecordedReplies.Jackett("caps").Single.Reply.Body!;
        Assert.Contains("<tv-search available=\"no\"", body);

        var result = ConnectionsEndpointRouteBuilderExtensions.ParseIndexerCaps(body);

        Assert.NotNull(result);
        Assert.Equal("movies", result.MediaScope);
    }

    [Fact]
    public void Reports_tv_only_when_movie_search_is_not_available()
    {
        // No tv-only indexer is recorded: the real movies-only document with its two searches swapped round.
        var body = RecordedReplies.Jackett("caps").Single.Reply.Body!
            .Replace("<tv-search available=\"no\"", "<tv-search available=\"yes\"")
            .Replace("<movie-search available=\"yes\"", "<movie-search available=\"no\"");

        var result = ConnectionsEndpointRouteBuilderExtensions.ParseIndexerCaps(body);

        Assert.NotNull(result);
        Assert.Equal("tv", result.MediaScope);
    }

    [Fact]
    public void Falls_back_to_nothing_when_the_body_has_no_categories()
    {
        var body = RecordedReplies.Jackett("caps").Single.Reply.Body!;
        var start = body.IndexOf("<categories>", StringComparison.Ordinal);
        var end = body.IndexOf("</categories>", StringComparison.Ordinal) + "</categories>".Length;

        var result = ConnectionsEndpointRouteBuilderExtensions.ParseIndexerCaps(body.Remove(start, end - start));

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
        // FetchIndexerCapsAsync checks for a Torznab/Newznab <error> element (t=caps not implemented, or refused)
        // before ever calling this parser; fed one directly it still finds no categories and yields nothing to
        // pre-fill rather than an error. These are the real refusals of a wrong key (Jackett and NZBHydra2).
        Assert.Null(ConnectionsEndpointRouteBuilderExtensions.ParseIndexerCaps(RecordedReplies.Jackett("caps-wrong-api-key").Single.Reply.Body!));
        Assert.Null(ConnectionsEndpointRouteBuilderExtensions.ParseIndexerCaps(RecordedReplies.Nzbhydra2("caps-wrong-api-key").Single.Reply.Body!));
        Assert.Null(ConnectionsEndpointRouteBuilderExtensions.ParseIndexerCaps(RecordedReplies.Jackett("unknown-function").Single.Reply.Body!));
    }
}
