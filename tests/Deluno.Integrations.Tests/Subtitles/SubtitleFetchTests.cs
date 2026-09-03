using System.IO.Compression;
using System.Net;
using System.Text;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Filesystem;
using Deluno.Integrations.Subtitles;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Integrations.Tests.Subtitles;

/// <summary>
/// What Deluno does with what a provider hands back.
///
/// <para>The three that matter are all failures a green suite would otherwise
/// miss: a provider serving an error page with a 200, a provider offering a
/// forced track as if it were coverage, and a source that is rate limiting being
/// treated as a source that is broken.</para>
/// </summary>
public sealed class SubtitleFetchTests
{
    private static readonly byte[] Srt = Encoding.UTF8.GetBytes(
        "1\r\n00:00:01,000 --> 00:00:03,000\r\nThe subtitle.\r\n");

    [Fact]
    public async Task Writes_the_first_usable_subtitle_and_stops_asking()
    {
        var first = new FakeProvider("first", [Candidate("en")], Srt);
        var second = new FakeProvider("second", [Candidate("en")], Srt);
        var writer = new FakeWriter();
        var service = Build([first, second], writer);

        var outcome = await service.FetchAsync(Request(), "en", false, @"D:\Media\Dune\Dune.mkv", false, CancellationToken.None);

        Assert.True(outcome.Found);
        Assert.Equal("first", outcome.ProviderKey);
        // Seven providers asked for one subtitle would be a better subtitle and
        // seven times the requests, and the sources that matter most are the
        // ones with a daily allowance.
        Assert.Equal(1, first.Searches);
        Assert.Equal(0, second.Searches);
    }

    [Fact]
    public async Task Applies_the_library_content_policy_before_writing()
    {
        var provider = new FakeProvider(
            "first",
            [Candidate("en")],
            Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:03,000\n<i>HELLO   WORLD</i> 😀\n[MUSIC]\n\n"));
        var writer = new FakeWriter();
        var service = Build([provider], writer);

        var outcome = await service.FetchAsync(
            Request(),
            "en",
            false,
            @"D:\Media\Dune\Dune.mkv",
            false,
            CancellationToken.None,
            new SubtitleContentModificationPolicy(
                StripHearingImpairedAnnotations: true,
                RemoveStyleTags: true,
                RemoveEmoji: true,
                NormalizeWhitespace: true,
                FixAllUppercase: true));

        var written = Encoding.UTF8.GetString(Assert.Single(writer.Written).Payload);
        Assert.True(outcome.Found);
        Assert.Contains("Hello world", written, StringComparison.Ordinal);
        Assert.DoesNotContain("[MUSIC]", written, StringComparison.Ordinal);
        Assert.DoesNotContain("<i>", written, StringComparison.Ordinal);
        Assert.Contains("style tags", outcome.AppliedModifications!);
    }

    [Fact]
    public async Task Refuses_an_error_page_a_provider_served_with_a_200()
    {
        // This is the failure that would otherwise be invisible: the bar goes
        // green over a file called Dune.en.srt containing a sign-in page, and
        // the player shows nothing.
        var html = Encoding.UTF8.GetBytes("<!DOCTYPE html><html><body>Please sign in to download</body></html>");
        var provider = new FakeProvider("first", [Candidate("en")], html);
        var writer = new FakeWriter();
        var service = Build([provider], writer);

        var outcome = await service.FetchAsync(Request(), "en", false, @"D:\Media\Dune\Dune.mkv", false, CancellationToken.None);

        Assert.False(outcome.Found);
        Assert.Empty(writer.Written);
        Assert.Equal(IntegrationFailureKind.MalformedResponse, Assert.Single(outcome.Failures!).Kind);
        Assert.Equal("download", outcome.Failure!.Operation);
    }

    [Fact]
    public async Task Unwraps_a_zip_because_half_of_them_send_one()
    {
        var provider = new FakeProvider("first", [Candidate("en")], Zip("Dune.srt", Srt));
        var writer = new FakeWriter();
        var service = Build([provider], writer);

        var outcome = await service.FetchAsync(Request(), "en", false, @"D:\Media\Dune\Dune.mkv", false, CancellationToken.None);

        Assert.True(outcome.Found);
        Assert.Equal(Srt, Assert.Single(writer.Written).Payload);
    }

    [Fact]
    public async Task Never_takes_a_forced_track_and_prefers_a_plain_one_over_hearing_impaired()
    {
        var provider = new FakeProvider("first",
        [
            // A file whose only English is forced has English for four lines of
            // Elvish, and the rest of Deluno already refuses to count it.
            Candidate("en") with { Forced = true, DownloadCount = 9999 },
            Candidate("en") with { HearingImpaired = true, DownloadCount = 500 },
            Candidate("en") with { DownloadCount = 10 }
        ], Srt);

        var writer = new FakeWriter();
        var service = Build([provider], writer);

        var outcome = await service.FetchAsync(Request(), "en", false, @"D:\Media\Dune\Dune.mkv", false, CancellationToken.None);

        Assert.True(outcome.Found);
        Assert.False(outcome.HearingImpaired);
        Assert.Equal("Dune.en.srt", Path.GetFileName(Assert.Single(writer.Written).Path));
    }

    [Fact]
    public async Task A_rate_limited_provider_is_working_and_is_left_alone()
    {
        var provider = new FakeProvider("first", [], Srt) { RateLimited = true };
        var repository = new FakeRepository([Connection("first")]);
        var service = Build([provider], new FakeWriter(), repository);

        await service.FetchAsync(Request(), "en", false, @"D:\Media\Dune\Dune.mkv", false, CancellationToken.None);

        var health = Assert.Single(repository.Health);
        Assert.Equal("rate-limited", health.Status);
        // Working, and asked to be left alone. Counting it as a failure would
        // eventually disable a source that never broke.
        Assert.True(health.Success);
        Assert.NotNull(health.RateLimitedUntil);
        Assert.Equal(IntegrationFailureKind.RateLimit, Assert.Single(repository.TypedFailures)!.Kind);
    }

    [Fact]
    public async Task A_tv_only_provider_is_not_asked_about_a_film()
    {
        // Gestdown returns nothing for a film and Yify nothing for an episode.
        // Asking anyway and counting the empty answer as a failure would mark
        // two working sources unhealthy on every cycle.
        var tvOnly = new FakeProvider("first", [Candidate("en")], Srt) { Scope = SubtitleProviderScope.TvOnly };
        var service = Build([tvOnly], new FakeWriter());

        var outcome = await service.FetchAsync(Request(), "en", isEpisodeMedia: false, @"D:\Media\Dune\Dune.mkv", false, CancellationToken.None);

        Assert.False(outcome.Found);
        Assert.Equal(0, tvOnly.Searches);
    }

    [Fact]
    public async Task A_source_that_cannot_be_reached_is_not_a_source_with_nothing()
    {
        // Learnt on the rig. Podnapisi's host stopped resolving, the search
        // swallowed the DNS failure and returned an empty list, and the screen
        // reported "answered but found nothing — that is usually wrong or
        // expired credentials". A confident wrong diagnosis of a site being
        // down, and worse than saying nothing.
        var unreachable = new FakeProvider("first", [], Srt) { Unreachable = true };
        var working = new FakeProvider("second", [Candidate("en") with { ProviderKey = "second" }], Srt);
        var repository = new FakeRepository([Connection("first"), Connection("second")]);
        var service = Build([unreachable, working], new FakeWriter(), repository);

        var outcome = await service.FetchAsync(Request(), "en", false, @"D:\Media\Dune\Dune.mkv", false, CancellationToken.None);

        Assert.Equal("failed", repository.Health[0].Status);
        Assert.False(repository.Health[0].Success);
        // And the next provider still gets asked, which is the whole reason an
        // unhelpful answer does not throw.
        Assert.True(outcome.Found);
        Assert.Equal("second", outcome.ProviderKey);
        Assert.Equal(IntegrationFailureKind.Unavailable, Assert.Single(outcome.Failures!).Kind);
        Assert.Null(outcome.Failure);
    }

    [Fact]
    public async Task Says_so_plainly_when_nothing_is_configured()
    {
        var service = Build([], new FakeWriter(), new FakeRepository([]));

        var outcome = await service.FetchAsync(Request(), "en", false, @"D:\Media\Dune\Dune.mkv", false, CancellationToken.None);

        Assert.False(outcome.Found);
        Assert.Contains("No subtitle providers", outcome.Reason, StringComparison.Ordinal);
        Assert.Equal(IntegrationFailureKind.Configuration, outcome.Failure!.Kind);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Http_errors_keep_their_status_for_typed_failure_classification(HttpStatusCode statusCode)
    {
        using var client = new HttpClient(new ResponseHandler(statusCode));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            SubtitleProviderHttp.GetJsonAsync<Dictionary<string, string>>(
                client,
                "https://subtitle.test/search",
                "provider",
                CancellationToken.None));

        Assert.Equal(statusCode, exception.StatusCode);
    }

    /* ------------------------------------------------------------ helpers */

    private static SubtitleSearchRequest Request()
        => new("Dune", 2021, null, null, null, null, null, ["en"], IsEpisode: false);

    [Fact]
    public async Task Refuses_a_release_the_library_said_never_to_take()
    {
        var provider = new FakeProvider(
            "first",
            [
                Candidate("en") with { ReleaseName = "Dune.2021.1080p.HDTV.x264-GROUP" },
                Candidate("en") with { DownloadToken = "keep", ReleaseName = "Dune.2021.1080p.BluRay-NTb" }
            ],
            Srt);
        var writer = new FakeWriter();
        var service = Build([provider], writer);

        var outcome = await service.FetchAsync(
            Request(),
            "en",
            false,
            @"D:\Media\Dune\Dune.2021.1080p.HDTV.x264-GROUP.mkv",
            false,
            CancellationToken.None,
            namePolicy: new SubtitleNamePolicy(MustNotContain: ["hdtv"]));

        Assert.True(outcome.Found);
        // The refused release is the one whose name matches the video file
        // exactly, so it would win on ranking. Filtering has to happen before
        // the ranking, not after it.
        Assert.Equal("keep", provider.LastDownloadToken);
    }

    [Fact]
    public async Task Takes_a_release_with_no_name_even_under_a_must_contain_list()
    {
        var provider = new FakeProvider("first", [Candidate("en")], Srt);
        var writer = new FakeWriter();
        var service = Build([provider], writer);

        var outcome = await service.FetchAsync(
            Request(),
            "en",
            false,
            @"D:\Media\Dune\Dune.mkv",
            false,
            CancellationToken.None,
            namePolicy: new SubtitleNamePolicy(MustContain: ["ntb"]));

        // Several providers return no release name at all. Refusing those would
        // silently empty the list for anybody who typed one term.
        Assert.True(outcome.Found);
    }

    [Fact]
    public async Task Reports_no_subtitle_rather_than_taking_a_refused_one()
    {
        var provider = new FakeProvider(
            "first",
            [Candidate("en") with { ReleaseName = "Dune.2021.CAM.x264" }],
            Srt);
        var service = Build([provider], new FakeWriter());

        var outcome = await service.FetchAsync(
            Request(),
            "en",
            false,
            @"D:\Media\Dune\Dune.mkv",
            false,
            CancellationToken.None,
            namePolicy: new SubtitleNamePolicy(MustNotContain: ["cam"]));

        Assert.False(outcome.Found);
    }

    [Theory]
    [InlineData("en", false, false, "Dune (2021).en.srt")]
    [InlineData("en", true, false, "Dune (2021).en.sdh.srt")]
    [InlineData("en", false, true, "Dune (2021).srt")]
    // The variant survives without the code: a plain and a hearing-impaired
    // subtitle are two different files, and dropping the distinction would have
    // the second silently overwrite the first.
    [InlineData("en", true, true, "Dune (2021).sdh.srt")]
    public void A_subtitle_is_named_after_the_video_it_belongs_to(
        string language, bool hearingImpaired, bool omitLanguageCode, string expected)
    {
        Assert.Equal(
            expected,
            SubtitleFileNaming.For(@"D:\Media\Dune\Dune (2021).mkv", language, hearingImpaired, omitLanguageCode));
    }

    private static SubtitleCandidate Candidate(string language)
        => new("first", "token", language, HearingImpaired: false, Forced: false);

    private static byte[] Zip(string name, byte[] content)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = archive.CreateEntry(name).Open();
            entry.Write(content);
        }

        return buffer.ToArray();
    }

    private static SubtitleProviderConnection Connection(string key)
        => new(key, key, key, null, null, null, 100, true, "untested", null, null, null, 0, null, null,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private static SubtitleFetchService Build(
        IReadOnlyList<FakeProvider> providers,
        FakeWriter writer,
        FakeRepository? repository = null)
        => new(
            new SubtitleProviderRegistry(providers),
            repository ?? new FakeRepository([.. providers.Select(provider => Connection(provider.Key))]),
            writer,
            TimeProvider.System,
            NullLogger<SubtitleFetchService>.Instance);

    private sealed class FakeProvider(string key, IReadOnlyList<SubtitleCandidate> results, byte[] payload) : ISubtitleProvider
    {
        public string Key => key;
        public string DisplayName => key;
        public string Description => key;
        public SubtitleProviderScope Scope { get; set; } = SubtitleProviderScope.Both;
        public SubtitleCredentialFields RequiredCredentials => SubtitleCredentialFields.None;
        public bool CredentialsOptional => false;
        public bool RateLimited { get; set; }
        public bool Unreachable { get; set; }
        public int Searches { get; private set; }
        public string? LastDownloadToken { get; private set; }

        public Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(
            SubtitleSearchRequest request, SubtitleProviderCredentials credentials, CancellationToken cancellationToken)
        {
            Searches++;
            if (RateLimited) throw new SubtitleProviderRateLimitedException(key, TimeSpan.FromMinutes(5));
            if (Unreachable) throw new HttpRequestException($"{key} could not be resolved.");
            return Task.FromResult(results);
        }

        public Task<byte[]> DownloadAsync(
            SubtitleCandidate candidate, SubtitleProviderCredentials credentials, CancellationToken cancellationToken)
        {
            LastDownloadToken = candidate.DownloadToken;
            return Task.FromResult(payload);
        }
    }

    private sealed class ResponseHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
    }

    private sealed class FakeWriter : ISubtitleFileWriter
    {
        public List<(string Path, byte[] Payload)> Written { get; } = [];

        public Task<string> WriteAsync(
            string videoPath,
            string language,
            bool hearingImpaired,
            byte[] subtitle,
            CancellationToken cancellationToken,
            bool omitLanguageCode = false)
        {
            // Through the real naming rule rather than a second copy of it, so
            // this fake cannot quietly disagree with what lands on disk.
            var path = Path.Combine(
                Path.GetDirectoryName(videoPath)!,
                SubtitleFileNaming.For(videoPath, language, hearingImpaired, omitLanguageCode));

            Written.Add((path, subtitle));
            return Task.FromResult(path);
        }
    }

    private sealed class FakeRepository(IReadOnlyList<SubtitleProviderConnection> rows) : ISubtitleProviderRepository
    {
        public List<(string Status, bool Success, DateTimeOffset? RateLimitedUntil)> Health { get; } = [];
        public List<IntegrationFailure?> TypedFailures { get; } = [];

        public Task<IReadOnlyList<SubtitleProviderConnection>> ListAsync(CancellationToken cancellationToken)
            => Task.FromResult(rows);

        public Task<SubtitleProviderConnection> SaveAsync(
            string providerKey, string displayName, SaveSubtitleProviderRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task RecordHealthAsync(
            string providerKey, string status, string? message, int? latencyMs, bool success,
            DateTimeOffset? rateLimitedUntilUtc, CancellationToken cancellationToken,
            IntegrationFailure? failure = null)
        {
            Health.Add((status, success, rateLimitedUntilUtc));
            TypedFailures.Add(failure);
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(string providerKey, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
