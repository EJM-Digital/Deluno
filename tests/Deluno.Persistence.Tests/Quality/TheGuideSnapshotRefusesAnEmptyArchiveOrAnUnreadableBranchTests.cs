using System.IO.Compression;
using System.Net;
using System.Text;
using Deluno.Quality.Guides;

namespace Deluno.Persistence.Tests.Quality;

/// <summary>
/// The TRaSH Guides snapshot (coverage register, engines): an archive that holds none of the tracked files, and a Git address that
/// does not name the master branch, are never taken as a snapshot. The client then tries the slower API route, which here says 404,
/// so the check ends in a refusal and no empty snapshot is ever returned. Served over the client factory; no guide content is invented.
/// </summary>
public sealed class TheGuideSnapshotRefusesAnEmptyArchiveOrAnUnreadableBranchTests
{
    private const string Revision = "0123456789abcdef0123456789abcdef01234567";

    private sealed class Handler(string refs, byte[]? zip) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);
            if (url.Contains("info/refs", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(refs) });
            }

            if (url.Contains("codeload.github.com", StringComparison.Ordinal) && zip is not null)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("https://api.github.com/") };
    }

    private static byte[] ZipWith(string path)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = archive.CreateEntry("Guides-" + Revision + "/" + path).Open();
            entry.Write(Encoding.UTF8.GetBytes("not tracked"));
        }

        return stream.ToArray();
    }

    [Fact]
    public async Task An_archive_with_none_of_the_tracked_files_is_never_returned_as_a_snapshot()
    {
        var handler = new Handler($"{Revision} refs/heads/master\n", ZipWith("README.md"));

        var thrown = await Assert.ThrowsAsync<HttpRequestException>(
            () => new GuideUpstreamTreeClient(new Factory(handler)).GetSnapshotAsync(CancellationToken.None));

        Assert.Contains("HTTP 404", thrown.Message);
        Assert.Contains(handler.Requests, url => url.Contains("codeload.github.com", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_git_address_that_does_not_name_the_master_branch_never_asks_for_an_archive()
    {
        var handler = new Handler($"{Revision} refs/heads/mastery\n{Revision} refs/heads/master-old\n", ZipWith("docs/json/radarr/cf/one.json"));

        var thrown = await Assert.ThrowsAsync<HttpRequestException>(
            () => new GuideUpstreamTreeClient(new Factory(handler)).GetSnapshotAsync(CancellationToken.None));

        Assert.Contains("HTTP 404", thrown.Message);
        Assert.DoesNotContain(handler.Requests, url => url.Contains("codeload.github.com", StringComparison.Ordinal));
    }
}
