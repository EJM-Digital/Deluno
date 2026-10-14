using System.Security.Cryptography;
using System.Text;
using Deluno.Companions;
using Deluno.Tests.Recorded;

namespace Deluno.Worker.Tests.Companions;

/// <summary>
/// #1301: every outcome of downloading a vendor build from GitHub, with GitHub's own recorded replies to a release download
/// (tests/fixtures/recorded/github): a file that arrives (the real SHA256SUMS.txt of Weir v1.0.0-rc.13 stands in for the
/// installer, which is a 20 MB binary the recording does not keep), a file GitHub answers 404 for (rc.17 was published with no
/// files), and what Deluno does with each: keeps it only when it is exactly the build, deletes anything else.
/// </summary>
public sealed class CompanionDownloaderOnRecordedGithubTests : IDisposable
{
    private const string Rc13 = "https://github.com/jampat000/Weir/releases/download/v1.0.0-rc.13/SHA256SUMS.txt";
    private const string Rc17 = "https://github.com/jampat000/Weir/releases/download/v1.0.0-rc.17/SHA256SUMS.txt";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "deluno-companion-download-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static CompanionDownloader Downloader()
        => new(new Factory(RecordedReplies.Handler("github", [RecordedReplies.Github("weir-newest-release-has-no-files")])));

    private static string RecordedFile(string url) => RecordedReplies.Github("weir-newest-release-has-no-files").Find(new Uri(url).AbsolutePath).Reply.Body!;

    private static CompanionArtifact Artifact(string url, string sha256, bool signed = false, long minimum = 1)
        => new("SHA256SUMS.txt", new Uri(url), sha256, signed, minimum);

    private static string HashOf(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public async Task A_file_that_is_exactly_the_pinned_build_is_downloaded_whole_and_kept()
    {
        var recorded = RecordedFile(Rc13);

        var path = await Downloader().DownloadAsync(Artifact(Rc13, HashOf(recorded)), _folder, CancellationToken.None);

        Assert.Equal(Path.Combine(_folder, "SHA256SUMS.txt"), path);
        Assert.Equal(recorded, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task A_file_GitHub_answers_404_for_is_a_plain_failure_naming_the_host_and_the_file_and_nothing_is_left()
    {
        var exception = await Assert.ThrowsAsync<CompanionDownloadException>(
            () => Downloader().DownloadAsync(Artifact(Rc17, HashOf("whatever")), _folder, CancellationToken.None));

        Assert.Equal("github.com answered HTTP 404 for SHA256SUMS.txt.", exception.Message);
        Assert.False(File.Exists(Path.Combine(_folder, "SHA256SUMS.txt")));
    }

    [Fact]
    public async Task A_file_that_is_not_the_build_the_catalogue_names_is_refused_and_deleted()
    {
        var exception = await Assert.ThrowsAsync<CompanionDownloadException>(
            () => Downloader().DownloadAsync(Artifact(Rc13, new string('A', 64)), _folder, CancellationToken.None));

        Assert.Contains("SHA-256 does not match", exception.Message);
        Assert.Contains("Nothing was installed", exception.Message);
        Assert.False(File.Exists(Path.Combine(_folder, "SHA256SUMS.txt")), "an unverified file is not left where something could run it");
    }

    [Fact]
    public async Task A_file_far_smaller_than_the_installer_is_refused_as_a_saved_error_page_and_deleted()
    {
        var recorded = RecordedFile(Rc13);

        var exception = await Assert.ThrowsAsync<CompanionDownloadException>(
            () => Downloader().DownloadAsync(Artifact(Rc13, HashOf(recorded), minimum: 20_000_000), _folder, CancellationToken.None));

        Assert.Contains("too small to be the installer", exception.Message);
        Assert.False(File.Exists(Path.Combine(_folder, "SHA256SUMS.txt")));
    }

    [Fact]
    public async Task A_build_that_must_be_signed_and_is_not_is_refused_and_deleted_even_when_its_hash_is_right()
    {
        var recorded = RecordedFile(Rc13);

        var exception = await Assert.ThrowsAsync<CompanionDownloadException>(
            () => Downloader().DownloadAsync(Artifact(Rc13, HashOf(recorded), signed: true), _folder, CancellationToken.None));

        Assert.Contains("does not carry a valid signature", exception.Message);
        Assert.False(File.Exists(Path.Combine(_folder, "SHA256SUMS.txt")));
    }

    [Fact]
    public async Task With_GitHub_unreachable_the_message_says_to_check_the_internet_connection_and_nothing_is_left()
    {
        // Nothing answers: the recording's own refusal of a connection, here GitHub's host not being reachable.
        var refused = RecordedReplies.Github("weir-newest-release-has-no-files");
        var handler = RecordedReplies.Handler("github", [refused], (_, _) => throw new HttpRequestException("No such host is known."));

        var exception = await Assert.ThrowsAsync<CompanionDownloadException>(
            () => new CompanionDownloader(new Factory(handler)).DownloadAsync(Artifact(Rc13, HashOf("x")), _folder, CancellationToken.None));

        Assert.Contains("could not download SHA256SUMS.txt from github.com", exception.Message);
        Assert.Contains("internet connection", exception.Message);
        Assert.False(File.Exists(Path.Combine(_folder, "SHA256SUMS.txt")));
    }
}
