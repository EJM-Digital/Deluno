using System.IO.Compression;
using System.Net;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Filesystem;
using Deluno.Integrations.Subtitles;
using Deluno.Integrations.Subtitles.Providers;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Integrations.Tests.Subtitles;

/// <summary>
/// #1301: the fetch service unwraps the archive a provider really sent. Subf2m answered Sintel's English subtitle with a real zip
/// (tests/fixtures/recorded/subf2m/sintel-english); the service searches, downloads it through the recorded redirect and writes the
/// subtitle that is inside the zip, not the zip.
/// </summary>
public sealed class SubtitleFetchOnARecordedSubf2mArchiveTests
{
    private static SubtitleSearchRequest Film() => new("Sintel", 2010, null, null, null, null, null, ["en"], IsEpisode: false);

    private static SubtitleFetchService Service(Recording recording, CapturingWriter writer, byte[]? archiveInstead = null)
    {
        HttpMessageHandler handler = new KeylessSubtitleDownloadsOnRecordedRepliesTests.Recorded(recording);
        if (archiveInstead is not null)
        {
            handler = new SwapArchive(handler, archiveInstead);
        }

        var provider = new Subf2mSubtitleProvider(new KeylessSubtitleDownloadsOnRecordedRepliesTests.Factory(handler));
        var row = new SubtitleProviderConnection("subf2m", "subf2m", "Subf2m", null, null, null, 100, true, "untested", null, null, null, 0, null, null,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        return new SubtitleFetchService(new SubtitleProviderRegistry([provider]), new Repository([row]), writer, TimeProvider.System, NullLogger<SubtitleFetchService>.Instance);
    }

    [Fact]
    public async Task The_zip_a_provider_really_sent_is_unwrapped_and_the_subtitle_inside_it_is_what_gets_written()
    {
        var recording = RecordedReplies.Subf2m("sintel-english");
        var zipBytes = recording.Exchanges[5].Reply.Bytes!;
        using var archive = new ZipArchive(new MemoryStream(zipBytes));
        var inside = archive.Entries.Single(entry => entry.Name.EndsWith(".srt", StringComparison.OrdinalIgnoreCase));
        using var insideBytes = new MemoryStream();
        await using (var stream = inside.Open())
        {
            await stream.CopyToAsync(insideBytes);
        }

        var writer = new CapturingWriter();
        var outcome = await Service(recording, writer).FetchAsync(Film(), "en", false, Path.Combine(Path.GetTempPath(), "Sintel (2010)", "Sintel (2010).mkv"),
            SubtitleHearingImpairedPreferences.PlainPreferred, CancellationToken.None);

        Assert.True(outcome.Found, outcome.Reason);
        var written = Assert.Single(writer.Written);
        Assert.Equal(insideBytes.ToArray(), written);
        Assert.NotEqual(zipBytes, written);
        Assert.Equal((byte)'P', zipBytes[0]);
    }

    [Fact]
    public async Task An_archive_that_is_not_a_zip_is_not_unpacked_and_is_refused_as_not_a_subtitle_rather_than_written()
    {
        // The recorded archive's own bytes, gzipped (the nearest recorded reply changed in the one value the test is about): a gz, rar or 7z is not handled by design.
        var recording = RecordedReplies.Subf2m("sintel-english");
        var zipBytes = recording.Exchanges[5].Reply.Bytes!;
        using var gzipped = new MemoryStream();
        await using (var gzip = new GZipStream(gzipped, CompressionLevel.Optimal, leaveOpen: true))
        {
            await gzip.WriteAsync(zipBytes);
        }

        var writer = new CapturingWriter();
        var outcome = await Service(recording, writer, gzipped.ToArray()).FetchAsync(Film(), "en", false, Path.Combine(Path.GetTempPath(), "Sintel (2010)", "Sintel (2010).mkv"),
            SubtitleHearingImpairedPreferences.PlainPreferred, CancellationToken.None);

        Assert.False(outcome.Found);
        Assert.Contains("not a subtitle", outcome.Reason, StringComparison.Ordinal);
        Assert.Empty(writer.Written);
    }

    /// <summary>Answers the download address (which the recording answers with a redirect to the archive) with other bytes; every other call is the recording's own reply.</summary>
    private sealed class SwapArchive(HttpMessageHandler recorded, byte[] bytes) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/download", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            }

            return await new HttpMessageInvoker(recorded, disposeHandler: false).SendAsync(request, cancellationToken);
        }
    }

    private sealed class CapturingWriter : ISubtitleFileWriter
    {
        public List<byte[]> Written { get; } = [];

        public Task<string> WriteAsync(string videoPath, string language, bool hearingImpaired, byte[] subtitle, CancellationToken cancellationToken, bool omitLanguageCode = false, bool forced = false)
        {
            Written.Add(subtitle);
            return Task.FromResult(Path.Combine(Path.GetDirectoryName(videoPath)!, SubtitleFileNaming.For(videoPath, language, hearingImpaired, omitLanguageCode, forced)));
        }
    }

    private sealed class Repository(IReadOnlyList<SubtitleProviderConnection> rows) : ISubtitleProviderRepository
    {
        public Task<IReadOnlyList<SubtitleProviderConnection>> ListAsync(CancellationToken cancellationToken) => Task.FromResult(rows);

        public Task<SubtitleProviderConnection> SaveAsync(string providerKey, string displayName, SaveSubtitleProviderRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task RecordHealthAsync(string providerKey, string status, string? message, int? latencyMs, bool success, DateTimeOffset? rateLimitedUntilUtc, CancellationToken cancellationToken, IntegrationFailure? failure = null)
            => Task.CompletedTask;

        public Task<bool> DeleteAsync(string providerKey, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
