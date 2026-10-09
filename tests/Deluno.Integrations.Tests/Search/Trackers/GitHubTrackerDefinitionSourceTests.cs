using System.IO.Compression;
using System.Net;
using System.Text;
using Deluno.Integrations.Search.Trackers;

namespace Deluno.Integrations.Tests.Search.Trackers;

/// <summary>#1181: tracker definitions come from one archive on codeload.github.com, which spends none of the API allowance. No real network.</summary>
public sealed class GitHubTrackerDefinitionSourceTests
{
    [Fact]
    public async Task The_definitions_come_from_the_archive_and_the_api_is_never_called()
    {
        var handler = new Script(request => request.RequestUri!.Host == "codeload.github.com"
            ? Zip(("Indexers-master/definitions/v11/alpha.yml", "id: alpha"), ("Indexers-master/definitions/v11/beta.yml", "id: beta"),
                ("Indexers-master/definitions/v10/old.yml", "id: old"), ("Indexers-master/definitions/v11/sub/deep.yml", "id: deep"), ("Indexers-master/README.md", "x"))
            : throw new InvalidOperationException("the API must not be called: " + request.RequestUri));
        var source = new GitHubTrackerDefinitionSource(new Factory(handler));

        var definitions = await source.FetchAsync(CancellationToken.None);

        Assert.Equal(["alpha", "beta"], definitions.Select(item => item.Id).Order().ToArray());
        Assert.Equal("id: alpha", definitions.First(item => item.Id == "alpha").Yaml);
        Assert.Equal(["codeload.github.com"], handler.Hosts);
    }

    [Fact]
    public async Task The_api_listing_is_used_only_when_the_archive_cannot_be_read()
    {
        var handler = new Script(request => request.RequestUri!.Host switch
        {
            "codeload.github.com" => new HttpResponseMessage(HttpStatusCode.NotFound),
            "api.github.com" => Json("""[{"name":"gamma.yml","type":"file","download_url":"https://raw.githubusercontent.com/x/gamma.yml"}]"""),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("id: gamma") }
        });
        var source = new GitHubTrackerDefinitionSource(new Factory(handler));

        var definitions = await source.FetchAsync(CancellationToken.None);

        Assert.Equal("gamma", Assert.Single(definitions).Id);
        Assert.Contains("api.github.com", handler.Hosts);
    }

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Zip(params (string Path, string Text)[] files)
    {
        var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, text) in files)
            {
                using var writer = new StreamWriter(archive.CreateEntry(path).Open());
                writer.Write(text);
            }
        }

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(buffer.ToArray()) };
    }

    private sealed class Script(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Hosts { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Hosts.Add(request.RequestUri!.Host);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
