using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Deluno.Quality.Guides;

namespace Deluno.Persistence.Tests.Quality;

/// <summary>
/// The limits on the TRaSH Guides source archive (coverage register, engines): an archive over 50 MB, a tracked file over 1 MB,
/// more than 20 MB of tracked data, a file that does not match its pinned blob, and an archive with none of the tracked files are
/// refused with a plain message. The archive is built here as a real zip served over the client factory; no guide content is invented.
/// </summary>
public sealed class TheGuideArchiveFetchRefusesWhatIsTooBigOrWrongTests
{
    private const string Revision = "0123456789abcdef0123456789abcdef01234567";
    private const string Tracked = "docs/json/radarr/cf/";

    private sealed class ZipHandler(byte[] zip, long? declaredLength = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new ByteArrayContent(zip);
            if (declaredLength is { } length)
            {
                content.Headers.ContentLength = length;
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("https://api.github.com/") };
    }

    private static byte[] Zip(params (string Path, byte[] Bytes)[] files)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, bytes) in files)
            {
                using var entry = archive.CreateEntry("Guides-" + Revision + "/" + path).Open();
                entry.Write(bytes);
            }
        }

        return stream.ToArray();
    }

    private static string BlobSha(byte[] bytes)
    {
        var header = Encoding.ASCII.GetBytes($"blob {bytes.Length}\0");
        return Convert.ToHexString(SHA1.HashData(header.Concat(bytes).ToArray())).ToLowerInvariant();
    }

    private static async Task<string> RefusalAsync(byte[] zip, GuideUpstreamTreeSnapshot snapshot, long? declaredLength = null)
    {
        var client = new GuideUpstreamTreeClient(new Factory(new ZipHandler(zip, declaredLength)));
        var thrown = await Assert.ThrowsAsync<InvalidDataException>(() => client.GetSourceInventoryAsync(snapshot, CancellationToken.None));
        return thrown.Message;
    }

    [Fact]
    public async Task An_archive_over_50_MB_a_file_over_1_MB_over_20_MB_of_files_or_a_file_that_does_not_match_its_pinned_blob_is_refused_in_plain_words()
    {
        var small = Encoding.UTF8.GetBytes("{}");
        var pinned = new GuideUpstreamTreeSnapshot(Revision, new Dictionary<string, string> { [Tracked + "one.json"] = BlobSha(small) });

        Assert.Contains("unexpectedly large", await RefusalAsync(Zip((Tracked + "one.json", small)), pinned, declaredLength: 51L * 1024 * 1024));

        var oneAndABit = new byte[1_024 * 1_024 + 1];
        Assert.Contains("one.json' is unexpectedly large", await RefusalAsync(Zip((Tracked + "one.json", oneAndABit)), pinned));

        var many = Enumerable.Range(0, 21).Select(index => ($"{Tracked}f{index}.json", new byte[1_000_000])).ToArray();
        Assert.Contains("retained TRaSH Guides source data is unexpectedly large", await RefusalAsync(Zip(many), new GuideUpstreamTreeSnapshot(Revision, new Dictionary<string, string>())));

        var changed = Encoding.UTF8.GetBytes("{\"changed\":true}");
        Assert.Contains("does not match pinned blob", await RefusalAsync(Zip((Tracked + "one.json", changed)), pinned));
    }
}
