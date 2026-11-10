using System.Net;
using System.Net.Http;
using System.Text;
using Deluno.Integrations.Search.Trackers;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Integrations.Tests.Search.Trackers;

/// <summary>
/// #1492: 1337x HTML-escapes a release name and then percent-encodes it in the magnet link, so after the page is
/// read the name still held "&amp;" and "&mdash;" and the download client named the torrent with those codes.
/// The page here is the real detail page, recorded from 1337x.
/// </summary>
public sealed class TrackerMagnetHtmlNameTests
{
    private const string Link = "https://1337x.st/torrent/6263793/Deadpool-Wolverine-2024-SDR-DS4K-1080p-BDRip-Hin-Eng-DDP-7-1-PeruGuy/";
    private const string PlainName = "Deadpool & Wolverine (2024) [SDR] DS4K 1080p BDRip [Hin-Eng] DDP 7.1 \u2014 PeruGuy";

    private static string Bundled1337x() => TrackerDefinitionCatalog.Bundled().Single(item => item.Id == "1337x").Yaml;

    private static async Task<TrackerGrab> GrabFrom(string page)
    {
        var definition = TrackerDefinition.Parse(Bundled1337x());
        var engine = new TrackerEngine(
            (_, _) => new Fake(request => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(page, Encoding.UTF8, "text/html"), RequestMessage = request }),
            TimeProvider.System,
            NullLogger<TrackerEngine>.Instance);
        var connection = new TrackerConnection("indexer-1", definition, definition.Links[0], new Dictionary<string, string?>());
        return await engine.ResolveDownloadAsync(connection, Link, CancellationToken.None);
    }

    private static string DisplayName(string magnet)
        => magnet.Split('?', 2)[1].Split('&').Select(pair => pair.Split('=', 2)).Where(pair => pair[0] == "dn").Select(pair => WebUtility.UrlDecode(pair[1])).Single();

    private static readonly string PageHash = RecordedReplies.Tracker1337x("detail-page-with-ampersand-and-dash").Matches("(?<=btih:)[0-9A-Fa-f]{40}")[0];

    private static string PageWithDn(string dn)
        => $"""<html><body><ul><li><a href="magnet:?xt=urn:btih:{PageHash}&amp;dn={dn}&amp;tr=udp%3A%2F%2Ftracker.example%3A1337%2Fannounce">Magnet Download</a></li></ul></body></html>""";

    [Fact]
    public async Task The_recorded_1337x_page_gives_a_magnet_named_with_the_plain_name()
    {
        var page = RecordedReplies.Tracker1337x("detail-page-with-ampersand-and-dash").Single.Reply.Body!;

        var grab = await GrabFrom(page);

        Assert.Equal(PlainName, DisplayName(grab.MagnetUrl!));
        Assert.DoesNotContain("amp", DisplayName(grab.MagnetUrl!), StringComparison.Ordinal);
        Assert.Equal(PageHash, TrackerMagnet.InfoHashOf(grab.MagnetUrl));
        // The trackers after the name are kept.
        Assert.Contains("&tr=udp%3A%2F%2Ftracker.opentrackr.org%3A1337%2Fannounce", grab.MagnetUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_name_the_tracker_escaped_with_a_literal_ampersand_code_is_decoded_once_only()
    {
        // A release that really is called "Tom &amp; Jerry": the page escapes its ampersand, so the page holds "&amp;amp;".
        var grab = await GrabFrom(PageWithDn("Tom+%26amp%3Bamp%3B+Jerry"));

        Assert.Equal("Tom &amp; Jerry", DisplayName(grab.MagnetUrl!));
    }

    [Fact]
    public async Task A_name_with_no_codes_is_left_as_it_is()
    {
        var grab = await GrabFrom(PageWithDn("Plain.Name.2024.1080p-GRP"));

        Assert.Equal("Plain.Name.2024.1080p-GRP", DisplayName(grab.MagnetUrl!));
    }

    [Theory]
    [InlineData("magnet:?xt=urn:btih:ABC&dn=A+%26amp%3B+B&tr=x", "A & B")]
    [InlineData("magnet:?dn=A+%26mdash%3B+B&xt=urn:btih:ABC", "A \u2014 B")]
    [InlineData("magnet:?xt=urn:btih:ABC&dn=A+%26%2338%3B+B", "A & B")]
    [InlineData("magnet:?xt=urn:btih:ABC&dn=Fast+%26+Furious", "Fast & Furious")]
    public void Normalize_decodes_the_display_name_once_after_percent_decoding(string magnet, string name)
        => Assert.Equal(name, DisplayName(TrackerMagnet.Normalize(magnet)));

    [Fact]
    public void Normalize_leaves_a_magnet_with_no_name_and_a_link_that_is_not_a_magnet_alone()
    {
        Assert.Equal("magnet:?xt=urn:btih:ABC&tr=x%26amp%3By", TrackerMagnet.Normalize("magnet:?xt=urn:btih:ABC&tr=x%26amp%3By"));
        Assert.Equal("https://x.example/a.torrent?dn=%26amp%3B", TrackerMagnet.Normalize("https://x.example/a.torrent?dn=%26amp%3B"));
    }

    private sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(answer(request));
    }
}
