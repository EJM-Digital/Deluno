using Deluno.Intake;

namespace Deluno.Platform.Tests.Intake;

public sealed class IntakeSourceAddressValidatorTests
{
    [Theory]
    [InlineData("tmdb", "12345")]
    [InlineData("tmdb", "https://www.themoviedb.org/list/12345")]
    [InlineData("tmdb-person", "6384")]
    [InlineData("tmdb-person", "https://www.themoviedb.org/person/6384?credits=cast,director")]
    [InlineData("trakt", "deluno-user")]
    [InlineData("trakt", "https://trakt.tv/users/deluno-user/lists/weekend")]
    [InlineData("mdblist", "https://mdblist.com/lists/ibtrashcan000/90-day-fiance")]
    [InlineData("letterboxd", "https://letterboxd.com/deluno/list/weekend/rss/")]
    [InlineData("rss", "https://example.test/feed.xml")]
    [InlineData("url-list", "http://example.test/titles.txt")]
    public void Accepts_supported_provider_addresses(string provider, string address)
    {
        Assert.Null(IntakeSourceAddressValidator.Validate(provider, address));
    }

    [Theory]
    [InlineData("tmdb", "arrival")]
    [InlineData("tmdb-person", "https://www.themoviedb.org/movie/603")]
    [InlineData("tmdb-person", "https://example.test/person/6384")]
    [InlineData("tmdb-person", "Keanu Reeves")]
    [InlineData("trakt", "https://example.test/list")]
    [InlineData("mdblist", "owner/list")]
    [InlineData("letterboxd", "https://example.test/list")]
    [InlineData("rss", "file:///C:/downloads/feed.xml")]
    [InlineData("url-list", "C:/downloads/titles.txt")]
    public void Rejects_addresses_that_the_selected_provider_cannot_handle(string provider, string address)
    {
        Assert.False(string.IsNullOrWhiteSpace(IntakeSourceAddressValidator.Validate(provider, address)));
    }

    [Theory]
    [InlineData("imdb", "ls12345678")]
    [InlineData("imdb", "https://www.imdb.com/list/ls12345678/export")]
    [InlineData("IMDb", "https://example.test/list.csv")]
    public void An_imdb_list_is_refused_with_the_plain_reason_and_the_alternative(string provider, string address)
    {
        Assert.Equal(IntakeSourceNotes.ImdbNoLongerSupported, IntakeSourceAddressValidator.Validate(provider, address));
        Assert.Contains("MDBList or Trakt", IntakeSourceNotes.ImdbNoLongerSupported);
    }

    [Theory]
    [InlineData("tmdb-popular", null)]
    [InlineData("TMDB-Popular", "  ")]
    [InlineData("tmdb-discover", "12,345,6")]
    [InlineData("tmdb-collection", "100")]
    [InlineData("tmdb-collection", "https://www.themoviedb.org/collection/100")]
    [InlineData("tmdb-company-credits", "420")]
    [InlineData("tmdb-company-credits", "https://tmdb.org/company/420")]
    [InlineData("stevenlu", "https://example.test/movies.json")]
    [InlineData("rss", "http://example.test/feed")]
    [InlineData("tmdb", "https://tmdb.org/list/12345")]
    [InlineData("trakt", "https://www.trakt.tv/users/a/lists/b")]
    public void Accepts_the_other_providers_including_ones_that_need_no_address(string provider, string? address)
    {
        Assert.Null(IntakeSourceAddressValidator.Validate(provider, address));
    }

    [Theory]
    [InlineData("tmdb", "https://example.test/list/12345")]
    [InlineData("tmdb-discover", "12,,5")]
    [InlineData("tmdb-collection", "abc")]
    [InlineData("tmdb-company-credits", "not a company")]
    [InlineData("stevenlu", "ftp://example.test/movies.json")]
    [InlineData("stevenlu", "movies.json")]
    [InlineData("trakt", "a")]
    [InlineData("mdblist", "https://example.test/lists/a/b")]
    [InlineData("letterboxd", "letterboxd")]
    [InlineData("tmdb", "ftp://themoviedb.org/list/1")]
    public void Refuses_a_wrong_address_with_the_providers_own_plain_reason(string provider, string address)
    {
        var reason = IntakeSourceAddressValidator.Validate(provider, address);
        Assert.False(string.IsNullOrWhiteSpace(reason));
        Assert.DoesNotContain("supported import-list provider", reason);
    }

    [Theory]
    [InlineData("tmdb", null)]
    [InlineData("tmdb", "")]
    [InlineData("rss", "   ")]
    public void A_missing_address_is_asked_for(string provider, string? address)
    {
        Assert.Equal("Add the source URL or identifier Deluno should poll.", IntakeSourceAddressValidator.Validate(provider, address));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("anilist")]
    public void An_unknown_or_missing_provider_is_refused(string? provider)
    {
        Assert.Equal("Choose a supported import-list provider.", IntakeSourceAddressValidator.Validate(provider, "https://example.test/x"));
    }
}
