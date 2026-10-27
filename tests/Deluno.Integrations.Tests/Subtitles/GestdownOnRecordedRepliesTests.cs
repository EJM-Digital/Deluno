using Deluno.Integrations.Subtitles;
using Deluno.Integrations.Subtitles.Providers;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.Subtitles;

/// <summary>
/// #1301: the Gestdown provider asked for a subtitle, against what Gestdown really answered (tests/fixtures/recorded/gestdown):
/// its show search and the English subtitles of Breaking Bad's first episode, and a show it does not know. Gestdown is TV only
/// and asks for no account. The subtitle file itself is not among the recordings, so what comes back from a download is not
/// tested here.
/// </summary>
public sealed class GestdownOnRecordedRepliesTests
{
    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static (GestdownSubtitleProvider Provider, List<string> Asked) Provider(params string[] states)
    {
        var asked = new List<string>();
        var handler = RecordedReplies.Handler("gestdown", states.Select(RecordedReplies.Gestdown), (request, _) =>
        {
            asked.Add(request.RequestUri!.PathAndQuery);
            return null;
        });
        return (new GestdownSubtitleProvider(new Factory(handler)), asked);
    }

    private static SubtitleSearchRequest Episode(string title, int? season = 1, int? episode = 1, params string[] languages)
        => new(title, null, season, episode, null, null, null, languages.Length == 0 ? ["en"] : languages, IsEpisode: true);

    [Fact]
    public async Task A_show_gestdown_knows_is_found_by_name_and_its_episode_subtitles_are_listed_with_their_download_links()
    {
        var (provider, asked) = Provider("breaking-bad-s01e01-english");
        var recorded = RecordedReplies.Gestdown("breaking-bad-s01e01-english").Exchanges[1].Reply.Json()["matchingSubtitles"]!.AsArray();

        var found = await provider.SearchAsync(Episode("Breaking Bad"), SubtitleProviderCredentials.None, CancellationToken.None);

        Assert.Equal(["/shows/search/Breaking%20Bad", "/subtitles/get/31ffb6ce-c000-4079-8912-b3f72057baed/1/1/en"], asked);
        Assert.Equal(recorded.Count, found.Count);
        Assert.All(found, candidate =>
        {
            Assert.Equal("gestdown", candidate.ProviderKey);
            Assert.Equal("en", candidate.Language);
            Assert.StartsWith("/subtitles/download/", candidate.DownloadToken, StringComparison.Ordinal);
        });
        var first = found[0];
        Assert.Equal((string?)recorded[0]!["version"], first.ReleaseName);
        Assert.Equal((string?)recorded[0]!["downloadUri"], first.DownloadToken);
        Assert.Equal((int?)recorded[0]!["downloadCount"], first.DownloadCount);
        Assert.Equal((bool)recorded[0]!["hearingImpaired"]!, first.HearingImpaired);
        Assert.False(first.Forced);
    }

    [Fact]
    public async Task A_show_gestdown_does_not_know_finds_nothing_and_asks_nothing_more()
    {
        var (provider, asked) = Provider("show-not-found");

        var found = await provider.SearchAsync(Episode("Zzzz Not A Show Zzzz"), SubtitleProviderCredentials.None, CancellationToken.None);

        Assert.Empty(found);
        Assert.Equal(["/shows/search/Zzzz%20Not%20A%20Show%20Zzzz"], asked);
    }

    [Fact]
    public async Task A_film_or_an_episode_without_a_season_and_number_is_not_a_question_gestdown_is_asked()
    {
        var (provider, asked) = Provider("breaking-bad-s01e01-english");

        var film = await provider.SearchAsync(new SubtitleSearchRequest("Sintel", 2010, null, null, null, null, null, ["en"], IsEpisode: false), SubtitleProviderCredentials.None, CancellationToken.None);
        var noNumber = await provider.SearchAsync(Episode("Breaking Bad", season: null, episode: null), SubtitleProviderCredentials.None, CancellationToken.None);

        Assert.Empty(film);
        Assert.Empty(noNumber);
        Assert.Empty(asked);
    }

    [Fact]
    public async Task Gestdown_is_TV_only_and_asks_for_no_account()
    {
        var (provider, _) = Provider("breaking-bad-s01e01-english");

        Assert.Equal(SubtitleProviderScope.TvOnly, provider.Scope);
        Assert.Equal(SubtitleCredentialFields.None, provider.RequiredCredentials);
        await Task.CompletedTask;
    }
}
