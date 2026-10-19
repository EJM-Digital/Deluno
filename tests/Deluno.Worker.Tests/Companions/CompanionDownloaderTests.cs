using System.Net;
using System.Security.Cryptography;
using Deluno.Companions;
using Moq;

namespace Deluno.Worker.Tests.Companions;

/// <summary>
/// The download step of setting up a tool, over a real folder: a file is kept only when it is the build the catalogue names, and a file that
/// is too small or has the wrong SHA-256 is deleted so a bad installer is never left to be run.
/// </summary>
public sealed class CompanionDownloaderTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "deluno-companion-downloader-tests", Guid.NewGuid().ToString("N"));

    private sealed class Serving(Func<HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(answer());
    }

    private static CompanionDownloader Downloader(Func<HttpResponseMessage> answer)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(item => item.CreateClient(CompanionDownloader.ClientName)).Returns(() => new HttpClient(new Serving(answer)));
        return new CompanionDownloader(factory.Object);
    }

    private static HttpResponseMessage Bytes(byte[] content) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };

    private static CompanionArtifact Artifact(byte[] content, long minimumBytes = 1, string? sha256 = null)
        => new("setup.exe", new Uri("https://downloads.example.test/setup.exe"), sha256 ?? Convert.ToHexString(SHA256.HashData(content)), false, minimumBytes);

    [Fact]
    public async Task The_build_the_catalogue_names_is_kept_exactly_as_downloaded()
    {
        var content = Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray();

        var path = await Downloader(() => Bytes(content)).DownloadAsync(Artifact(content), _folder, CancellationToken.None);

        Assert.Equal(Path.Combine(_folder, "setup.exe"), path);
        Assert.Equal(content, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task A_file_that_is_too_small_is_refused_in_plain_words_and_deleted()
    {
        var content = new byte[2048];

        var failure = await Assert.ThrowsAsync<CompanionDownloadException>(() =>
            Downloader(() => Bytes(content)).DownloadAsync(Artifact(content, minimumBytes: 1_000_000), _folder, CancellationToken.None));

        Assert.Contains("is 2,048 bytes, too small to be the installer. Nothing was installed.", failure.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_folder, "setup.exe")), "a bad installer is not left on disk");
    }

    [Fact]
    public async Task A_file_with_the_wrong_SHA_256_is_refused_in_plain_words_and_deleted()
    {
        var content = new byte[4096];
        Array.Fill(content, (byte)7);

        var failure = await Assert.ThrowsAsync<CompanionDownloadException>(() =>
            Downloader(() => Bytes(content)).DownloadAsync(Artifact(content, sha256: new string('A', 64)), _folder, CancellationToken.None));

        Assert.Contains("is not the build Deluno expects (its SHA-256 does not match). Nothing was installed.", failure.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_folder, "setup.exe")));
    }

    [Fact]
    public async Task A_server_that_answers_with_an_error_names_the_host_and_leaves_nothing_behind()
    {
        var failure = await Assert.ThrowsAsync<CompanionDownloadException>(() =>
            Downloader(() => new HttpResponseMessage(HttpStatusCode.NotFound)).DownloadAsync(Artifact(new byte[10]), _folder, CancellationToken.None));

        Assert.Equal("downloads.example.test answered HTTP 404 for setup.exe.", failure.Message);
        Assert.False(File.Exists(Path.Combine(_folder, "setup.exe")));
    }

    [Fact]
    public async Task A_download_that_cannot_reach_the_server_says_to_check_the_connection_and_leaves_nothing_behind()
    {
        var failure = await Assert.ThrowsAsync<CompanionDownloadException>(() =>
            Downloader(() => throw new HttpRequestException("no route")).DownloadAsync(Artifact(new byte[10]), _folder, CancellationToken.None));

        Assert.Contains("Check this computer's internet connection", failure.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_folder, "setup.exe")));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }
}
