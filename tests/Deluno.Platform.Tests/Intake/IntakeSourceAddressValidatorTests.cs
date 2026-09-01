using Deluno.Intake;

namespace Deluno.Platform.Tests.Intake;

public sealed class IntakeSourceAddressValidatorTests
{
    [Theory]
    [InlineData("tmdb", "12345")]
    [InlineData("tmdb", "https://www.themoviedb.org/list/12345")]
    [InlineData("tmdb-person", "6384")]
    [InlineData("tmdb-person", "https://www.themoviedb.org/person/6384?credits=cast,director")]
    [InlineData("imdb", "ls12345678")]
    [InlineData("imdb", "https://www.imdb.com/list/ls12345678/export")]
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
    [InlineData("imdb", "https://example.test/list.csv")]
    [InlineData("trakt", "https://example.test/list")]
    [InlineData("mdblist", "owner/list")]
    [InlineData("letterboxd", "https://example.test/list")]
    [InlineData("rss", "file:///C:/downloads/feed.xml")]
    [InlineData("url-list", "C:/downloads/titles.txt")]
    public void Rejects_addresses_that_the_selected_provider_cannot_handle(string provider, string address)
    {
        Assert.False(string.IsNullOrWhiteSpace(IntakeSourceAddressValidator.Validate(provider, address)));
    }
}
