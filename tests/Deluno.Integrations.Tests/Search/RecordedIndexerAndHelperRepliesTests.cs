using System.Xml.Linq;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.Search;

/// <summary>
/// What a real Torznab indexer (Jackett 0.24.2850 over public trackers), a real Newznab server (NZBHydra2 9.1.1) and a real
/// Cloudflare helper (FlareSolverr 3.5.2) answer, pinned from their recordings (#1237): every recorded state is read here, so a
/// reply Deluno's tests rely on cannot drift from what the app sent. The searches are whatever the public trackers returned on the
/// day; the assertions are on the format, never on a particular release.
/// </summary>
public sealed class RecordedIndexerAndHelperRepliesTests
{
    private static XDocument Xml(Recording recording, int exchange = 0) => XDocument.Parse(recording.Exchanges[exchange].Reply.Body!);

    private static int Items(XDocument document) => document.Descendants("item").Count();

    [Fact]
    public void Jackett_answers_a_feed_a_text_search_and_the_movie_and_tv_searches_as_rss_with_torznab_attributes()
    {
        foreach (var (state, exchange) in new[] { ("search-feed", 0), ("search-text", 0), ("search-movie-imdb", 0), ("search-movie-imdb", 1), ("search-tv", 0), ("search-tv", 1) })
        {
            var document = Xml(RecordedReplies.Jackett(state), exchange);
            Assert.Equal("rss", document.Root!.Name.LocalName);
            Assert.True(Items(document) > 0, $"{state} #{exchange} has releases");
            Assert.Contains(document.Descendants(), element => element.Name.LocalName == "attr" && element.Name.NamespaceName.Contains("torznab", StringComparison.Ordinal));
        }

        Assert.Equal(0, Items(Xml(RecordedReplies.Jackett("search-no-results"))));
        Assert.Equal(200, RecordedReplies.Jackett("search-feed").Single.Reply.Status);
    }

    [Fact]
    public void Jackett_answers_the_errors_a_key_a_function_and_an_indexer_get_wrong_as_torznab_error_elements()
    {
        Assert.Equal("100", Xml(RecordedReplies.Jackett("caps-wrong-api-key")).Root!.Attribute("code")!.Value);
        Assert.Equal("100", Xml(RecordedReplies.Jackett("caps-no-api-key")).Root!.Attribute("code")!.Value);
        Assert.Equal("201", Xml(RecordedReplies.Jackett("unknown-function")).Root!.Attribute("code")!.Value);
        var unknown = RecordedReplies.Jackett("unknown-indexer").Single.Reply;
        Assert.Equal(500, unknown.Status);
        Assert.Contains("Unknown indexer", unknown.Body);
    }

    [Fact]
    public void Jackett_caps_name_the_search_modes_and_categories_and_the_tv_tracker_supports_season_and_episode()
    {
        var tv = Xml(RecordedReplies.Jackett("caps-tv"));
        Assert.Contains("season", tv.Descendants("tv-search").Single().Attribute("supportedParams")!.Value);
        Assert.Contains(tv.Descendants("category"), category => category.Attribute("id")!.Value == "5000");
        Assert.NotEmpty(Xml(RecordedReplies.Jackett("caps-all")).Descendants("category"));
        Assert.Equal("Jackett", Xml(RecordedReplies.Jackett("caps")).Descendants("server").Single().Attribute("title")!.Value);
    }

    [Fact]
    public void Jackett_reports_its_version_and_hands_a_result_link_back_as_a_download()
    {
        Assert.Equal("0.24.2850", RecordedReplies.Jackett("server-config").Single.Reply.Json()["app_version"]!.GetValue<string>().TrimStart('v'));
        var download = RecordedReplies.Jackett("download-link").Single;
        Assert.Equal(200, download.Reply.Status);
        Assert.StartsWith("/dl/", download.Request.Path, StringComparison.Ordinal);
    }

    [Fact]
    public void NZBHydra2_answers_the_newznab_searches_and_errors_in_the_same_envelope()
    {
        foreach (var state in new[] { "search-feed", "search-text", "search-movie-imdb", "search-tv" })
        {
            var document = Xml(RecordedReplies.Nzbhydra2(state));
            Assert.Equal("rss", document.Root!.Name.LocalName);
        }

        Assert.Equal("100", Xml(RecordedReplies.Nzbhydra2("caps-no-api-key")).Root!.Attribute("code")!.Value);
        Assert.Equal("100", Xml(RecordedReplies.Nzbhydra2("caps-wrong-api-key")).Root!.Attribute("code")!.Value);
        Assert.Equal("error", Xml(RecordedReplies.Nzbhydra2("unknown-function")).Root!.Name.LocalName);
        Assert.NotEmpty(Xml(RecordedReplies.Nzbhydra2("caps")).Descendants("category"));
    }

    [Fact]
    public void FlareSolverr_answers_with_the_status_message_and_solution_envelope()
    {
        var index = RecordedReplies.Flaresolverr("index").Single.Reply.Json();
        Assert.EndsWith("is ready!", index["msg"]!.GetValue<string>());
        Assert.Equal("3.5.2", index["version"]!.GetValue<string>());

        foreach (var state in new[] { "request-get-ok", "request-get-no-session", "request-post", "request-session-missing", "challenge-not-seen" })
        {
            var reply = RecordedReplies.Flaresolverr(state).Single.Reply.Json();
            Assert.Equal("ok", reply["status"]!.GetValue<string>());
            Assert.Equal(200, reply["solution"]!["status"]!.GetValue<int>());
            Assert.NotNull(reply["solution"]!["response"]);
            Assert.NotNull(reply["startTimestamp"]);
        }

        Assert.Equal("Challenge not detected!", RecordedReplies.Flaresolverr("challenge-not-seen").Single.Reply.Json()["message"]!.GetValue<string>());
    }

    [Fact]
    public void FlareSolverr_answers_what_it_cannot_do_with_status_500_and_its_reason()
    {
        foreach (var state in new[] { "request-host-missing", "request-no-url", "request-timeout", "unknown-command" })
        {
            var reply = RecordedReplies.Flaresolverr(state).Single.Reply;
            Assert.Equal(500, reply.Status);
            Assert.Equal("error", reply.Json()["status"]!.GetValue<string>());
        }

        Assert.Contains("ERR_NAME_NOT_RESOLVED", RecordedReplies.Flaresolverr("request-host-missing").Single.Reply.Body);
        Assert.Contains("Timeout", RecordedReplies.Flaresolverr("request-timeout").Single.Reply.Body);
        Assert.All(RecordedReplies.Flaresolverr("bad-body").Exchanges, e => Assert.Equal(500, e.Reply.Status));
        Assert.Equal(405, RecordedReplies.Flaresolverr("wrong-method").Single.Reply.Status);
        Assert.Empty(RecordedReplies.Flaresolverr("sessions-empty").Single.Reply.Json()["sessions"]!.AsArray());
    }

    [Fact]
    public void FlareSolverr_not_answering_is_a_refused_connection()
    {
        Assert.Equal(0, RecordedReplies.Flaresolverr("not-answering").Single.Reply.Status);
    }
}
