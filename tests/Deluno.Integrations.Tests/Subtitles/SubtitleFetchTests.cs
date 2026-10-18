using System.IO.Compression;
using System.Net;
using System.Text;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Filesystem;
using Deluno.Integrations.Subtitles;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
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

        var outcome = await service.FetchAsync(Request(), "en", false, @"D:\Media\Dune\Dune.mkv", SubtitleHearingImpairedPreferences.PlainPreferred, CancellationToken.None);

        Assert.True(outcome.Found);
        Assert.Equal("first", outcome.ProviderKey);
        // Seven providers asked for one subtitle would be a better subtitle and
        // seven times the requests, and the sources that matter most are the
        // ones with a daily allowance.
        Assert.Equal(1, first.Searches);
        Assert.Equal(0, second.Searches);
    }

    [Fact]
    public async Task A_subtitle_that_is_written_is_recorded_in_activity_with_its_title_file_and_provider()
    {
        var provider = new FakeProvider("first", [Candidate("en")], Srt);
        var activity = new RecordingActivityFeedRepository();
        var service = new SubtitleFetchService(
            new SubtitleProviderRegistry([provider]), new FakeRepository([Connection("first")]), new FakeWriter(),
            TimeProvider.System, NullLogger<SubtitleFetchService>.Instance, activity);

        await service.FetchAsync(Request(), "en", false, @"D:MediaDuneDune.mkv", SubtitleHearingImpairedPreferences.PlainPreferred, CancellationToken.None);

        var recorded = Assert.Single(activity.Recorded);
        Assert.Equal("subtitle.fetched", recorded.Category);
        Assert.Contains("Dune", recorded.Message, StringComparison.Ordinal);
        Assert.Contains("first", recorded.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_subtitle_that_is_written_carries_the_file_it_wrote_into_activity_so_everything_about_that_file_finds_it()
    {
        var provider = new FakeProvider("first", [Candidate("en")], Srt);
        var activity = new RecordingActivityFeedRepository();
        var writer = new FakeWriter();
        var service = new SubtitleFetchService(
            new SubtitleProviderRegistry([provider]), new FakeRepository([Connection("first")]), writer,
            TimeProvider.System, NullLogger<SubtitleFetchService>.Instance, activity);

        var outcome = await service.FetchAsync(Request(), "en", false, @"D:\Media\Dune\Dune.mkv", SubtitleHearingImpairedPreferences.PlainPreferred, CancellationToken.None);

        Assert.True(outcome.Found);
        var facts = ActivityHistory.FactsFromDetails(Assert.Single(activity.Details));
        Assert.Equal(outcome.WrittenPath, facts.FilePaths);
        Assert.Equal("first", facts.SourceName);
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
            SubtitleHearingImpairedPreferences.PlainPreferred,
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

        var outcome = await service.FetchAsync(Request(), "en", false, @"D:\Media\Dune\Dune.mkv", SubtitleHearingImpairedPreferences.PlainPreferred, CancellationToken.None);

        Assert.False(outcome.Found);
        Assert.Empty(writer.Written);
        Assert.Equal(IntegrationFailureKind.MalformedResponse, Assert.Single(outcome.Failures!).Kind);
        Assert.Equal("download", outcome.Failure!.Operation);
    }

    /// <summary>
    /// Before #898 a malformed download gave up on the whole provider. A
    /// provider can offer several candidates for one language, and a bad one
    /// among them is not evidence the rest are bad too.
    /// </summary>
    [Fact]
    public async Task A_malformed_candidate_does_not_give_up_on_the_rest_of_the_provider()
    {
        var html = Encoding.UTF8.GetBytes("<!DOCTYPE html><html><body>Please sign in</body></html>");
        var provider = new FakeProvider(
            "first",
            [
                Candidate("en") with { DownloadToken = "bad", DownloadCount = 999 },
                Candidate("en") with { DownloadToken = "good", DownloadCount = 1 }
            ],
            Srt);
        provider.PayloadByToken["bad"] = html;
        provider.PayloadByToken["good"] = Srt;
        var writer = new FakeWriter();
        var service = Build([provider], writer);

        var outcome = await service.FetchAsync(Request(), "en", false, @"D:\Media\Dune\Dune.mkv", SubtitleHearingImpairedPreferences.PlainPreferred, CancellationToken.None);

        Assert.True(outcome.Found);
        Assert.Equal("good", provider.LastDownloadToken);
        var rejected = Assert.Single(outcome.RejectedCandidates!);
        Assert.Equal("first|bad", rejected.BlockKey);
        Assert.Equal("first", rejected.ProviderKey);
    }

    [Fact]
    public async Task A_blocked_candidate_is_never_offered_again()
    {
        var provider = new FakeProvider(
            "first",
            [
                Candidate("en") with { DownloadToken = "blocked", DownloadCount = 999 },
                Candidate("en") with { DownloadToken = "ok", DownloadCount = 1 }
            ],
            Srt);
        var writer = new FakeWriter();
        var service = Build([provider], writer);

        var outcome = await service.FetchAsync(
            Request(), "en", false, @"D:\Media\Dune\Dune.mkv", SubtitleHearingImpairedPreferences.PlainPreferred, CancellationToken.None,
            blockedKeys: ["first|blocked"]);

        Assert.True(outcome.Found);
        Assert.Equal("ok", provider.LastDownloadToken);
    }

    [Fact]
    public async Task Unwraps_a_zip_because_half_of_them_send_one()
    {
        var provider = new FakeProvider("first", [Candidate("en")], Zip("Dune.srt", Srt));
        var writer = new FakeWriter();
        var service = Build([provider], writer);

        var outcome = await service.FetchAsync(Request(), "en", false, @"D:\Media\Dune\Dune.mkv", SubtitleHearingImpairedPreferences.PlainPreferred, CancellationToken.None);

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

        var outcome = await service.FetchAsync(Request(), "en", false, @"D:\Media\Dune\Dune.mkv", SubtitleHearingImpairedPreferences.PlainPreferred, CancellationToken.None);

        Assert.True(outcome.Found);
        Assert.False(outcome.HearingImpaired);
        Assert.Equal("Dune.en.srt", Path.GetFileName(Assert.Single(writer.Written).Path));
    }

    [Fact]
    public async Task Plain_only_refuses_a_hearing_impaired_candidate_and_takes_the_next_one()
    {
        // "Wrong type is rejected and the next fetched" (#923): there is no
        // blocklist step needed for this — the wrong type is filtered before
        // ranking, so a plain candidate from the same provider is simply what
        // gets picked instead.
        var provider = new FakeProvider("first",
        [
            Candidate("en") with { HearingImpaired = true, DownloadToken = "hi", DownloadCount = 999 },
            Candidate("en") with { DownloadToken = "plain", DownloadCount = 1 }
        ], Srt);
        var writer = new FakeWriter();
        var service = Build([provider], writer);

        var outcome = await service.FetchAsync(
            Request(), "en", false, @"D:\Media\Dune\Dune.mkv",
            SubtitleHearingImpairedPreferences.PlainOnly, CancellationToken.None);

        Assert.True(outcome.Found);
        Assert.False(outcome.HearingImpaired);
        Assert.Equal("plain", provider.LastDownloadToken);
    }

    [Fact]
    public async Task Plain_only_reports_no_subtitle_when_every_candidate_is_hearing_impaired()
    {
        var provider = new FakeProvider("first",
            [Candidate("en") with { HearingImpaired = true }], Srt);
        var service = Build([provider], new FakeWriter());

        var outcome = await service.FetchAsync(
            Request(), "en", false, @"D:\Media\Dune\Dune.mkv",
            SubtitleHearingImpairedPreferences.PlainOnly, CancellationToken.None);

        Assert.False(outcome.Found);
    }

    [Fact]
    public async Task Hearing_impaired_preferred_takes_it_over_a_plain_one_on_a_tie()
    {
        var provider = new FakeProvider("first",
        [
            Candidate("en") with { DownloadToken = "plain", DownloadCount = 500 },
            Candidate("en") with { HearingImpaired = true, DownloadToken = "hi", DownloadCount = 10 }
        ], Srt);
        var writer = new FakeWriter();
        var service = Build([provider], writer);

        var outcome = await service.FetchAsync(
            Request(), "en", false, @"D:\Media\Dune\Dune.mkv",
            SubtitleHearingImpairedPreferences.HearingImpairedPreferred, CancellationToken.None);

        Assert.True(outcome.Found);
        Assert.True(outcome.HearingImpaired);
        Assert.Equal("hi", provider.LastDownloadToken);
    }

    [Fact]
    public async Task Hearing_impaired_preferred_never_beats_a_better_release_match()
    {
        // The rung is said out loud for a reason: preference only breaks a
        // tie, it never outranks a subtitle actually cut for this release.
        var provider = new FakeProvider("first",
        [
            Candidate("en") with { DownloadToken = "hi", HearingImpaired = true, ReleaseName = "Dune.2021.WEBRip" },
            Candidate("en") with { DownloadToken = "plain", ReleaseName = "Dune.2021.1080p.BluRay-NTb" }
        ], Srt);
        var writer = new FakeWriter();
        var service = Build([provider], writer);

        var outcome = await service.FetchAsync(
            Request(), "en", false, @"D:\Media\Dune\Dune.2021.1080p.BluRay-NTb.mkv",
            SubtitleHearingImpairedPreferences.HearingImpairedPreferred, CancellationToken.None);

        Assert.True(outcome.Found);
        Assert.False(outcome.HearingImpaired);
        Assert.Equal("plain", provider.LastDownloadToken);
    }

    [Fact]
    public async Task FetchForcedAsync_only_takes_a_forced_candidate_and_writes_it_as_an_extra_file()
    {
        var provider = new FakeProvider("first",
        [
            Candidate("en") with { DownloadToken = "plain", DownloadCount = 999 },
            Candidate("en") with { Forced = true, DownloadToken = "forced", DownloadCount = 1 }
        ], Srt);
        var writer = new FakeWriter();
        var service = Build([provider], writer);

        var outcome = await service.FetchForcedAsync(Request(), "en", false, @"D:\Media\Dune\Dune.mkv", CancellationToken.None);

        Assert.True(outcome.Found);
        Assert.Equal("forced", provider.LastDownloadToken);
        Assert.Equal("Dune.en.forced.srt", Path.GetFileName(Assert.Single(writer.Written).Path));
    }

    [Fact]
    public async Task FetchForcedAsync_reports_no_subtitle_when_nothing_is_forced()
    {
        var provider = new FakeProvider("first", [Candidate("en")], Srt);
        var service = Build([provider], new FakeWriter());

        var outcome = await service.FetchForcedAsync(Request(), "en", false, @"D:\Media\Dune\Dune.mkv", CancellationToken.None);

        Assert.False(outcome.Found);
    }

    [Fact]
    public async Task Plain_only_takes_a_hearing_impaired_candidate_when_stripping_will_make_it_plain()
    {
        // #923's strip-counts-as-plain rule: a language set to plain-only
        // still refuses hearing-impaired text on disk, but not a candidate
        // that will no longer be hearing-impaired once the strip runs.
        var provider = new FakeProvider("first",
            [Candidate("en") with { HearingImpaired = true }],
            Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:03,000\n[MUSIC] Hello.\n"));
        var writer = new FakeWriter();
        var service = Build([provider], writer);

        var outcome = await service.FetchAsync(
            Request(), "en", false, @"D:\Media\Dune\Dune.mkv",
            SubtitleHearingImpairedPreferences.PlainOnly, CancellationToken.None,
            new SubtitleContentModificationPolicy(StripHearingImpairedAnnotations: true));

        Assert.True(outcome.Found);
        // Recorded and named as plain, not as hearing-impaired that merely
        // reads clean — the two are told apart everywhere this rung is read.
        Assert.False(outcome.HearingImpaired);
        Assert.Equal("Dune.en.srt", Path.GetFileName(Assert.Single(writer.Written).Path));
    }

    [Fact]
    public async Task Plain_only_still_refuses_hearing_impaired_when_nothing_strips_it()
    {
        var provider = new FakeProvider("first", [Candidate("en") with { HearingImpaired = true }], Srt);
        var service = Build([provider], new FakeWriter());

        var outcome = await service.FetchAsync(
            Request(), "en", false, @"D:\Media\Dune\Dune.mkv",
            SubtitleHearingImpairedPreferences.PlainOnly, CancellationToken.None);

        Assert.False(outcome.Found);
    }

    [Fact]
    public async Task An_authentication_failure_is_written_to_activity_once_it_is_recorded()
    {
        // #896: the repository is what actually pauses the provider (proven
        // in IntegrationHealthPersistenceTests); this proves the other half —
        // that a person is told about it, in Activity, in plain words.
        var provider = new FakeProvider("first", [], Srt) { ThrownException = new HttpRequestException(
            "Unauthorized", null, System.Net.HttpStatusCode.Unauthorized) };
        var repository = new FakeRepository([Connection("first")]);
        var activity = new RecordingActivityFeedRepository();
        var service = new SubtitleFetchService(
            new SubtitleProviderRegistry([provider]), repository, new FakeWriter(),
            TimeProvider.System, NullLogger<SubtitleFetchService>.Instance, activity);

        await service.FetchAsync(
            Request(), "en", false, @"D:\Media\Dune\Dune.mkv",
            SubtitleHearingImpairedPreferences.PlainPreferred, CancellationToken.None);

        var recorded = Assert.Single(activity.Recorded);
        Assert.Equal("subtitle.provider.paused", recorded.Category);
        Assert.Contains("first", recorded.Message, StringComparison.Ordinal);
        Assert.Equal("subtitle-provider", recorded.RelatedEntityType);
        Assert.Equal("first", recorded.RelatedEntityId);
    }

    [Fact]
    public async Task A_non_authentication_failure_is_never_written_to_activity()
    {
        var provider = new FakeProvider("first", [], Srt) { Unreachable = true };
        var repository = new FakeRepository([Connection("first")]);
        var activity = new RecordingActivityFeedRepository();
        var service = new SubtitleFetchService(
            new SubtitleProviderRegistry([provider]), repository, new FakeWriter(),
            TimeProvider.System, NullLogger<SubtitleFetchService>.Instance, activity);

        await service.FetchAsync(
            Request(), "en", false, @"D:\Media\Dune\Dune.mkv",
            SubtitleHearingImpairedPreferences.PlainPreferred, CancellationToken.None);

        Assert.Empty(activity.Recorded);
    }

    [Fact]
    public async Task A_rate_limited_provider_is_working_and_is_left_alone()
    {
        var provider = new FakeProvider("first", [], Srt) { RateLimited = true };
        var repository = new FakeRepository([Connection("first")]);
        var service = Build([provider], new FakeWriter(), repository);

        await service.FetchAsync(Request(), "en", false, @"D:\Media\Dune\Dune.mkv", SubtitleHearingImpairedPreferences.PlainPreferred, CancellationToken.None);

        var health = Assert.Single(repository.Health);
        Assert.Equal("rate-limited", health.Status);
        // Working, and asked to be left alone. Counting it as a failure would
        // eventually disable a source that never broke.
        Assert.True(health.Success);
        Assert.NotNull(health.RateLimitedUntil);
        Assert.Equal(IntegrationFailureKind.RateLimit, Assert.Single(repository.TypedFailures)!.Kind);
    }

    /// <summary>
    /// #946: OpenSubtitles' 406 answer for a spent daily allowance pauses the
    /// provider and writes the same Activity line an authentication pause
    /// does — the other half of #896's "actually pause a provider" promise,
    /// which only ever covered a wrong login.
    /// </summary>
    [Fact]
    public async Task A_quota_exhausted_download_pauses_the_provider_and_is_written_to_activity()
    {
        var provider = new FakeProvider("first", [Candidate("en")], Srt)
        {
            ThrownDownloadException = new SubtitleProviderQuotaExhaustedException(
                "first", "The daily download allowance is used up for today.",
                DateTimeOffset.Parse("2026-04-30T00:00:00Z"))
        };
        var repository = new FakeRepository([Connection("first")]);
        var activity = new RecordingActivityFeedRepository();
        var service = new SubtitleFetchService(
            new SubtitleProviderRegistry([provider]), repository, new FakeWriter(),
            TimeProvider.System, NullLogger<SubtitleFetchService>.Instance, activity);

        var outcome = await service.FetchAsync(
            Request(), "en", false, @"D:\Media\Dune\Dune.mkv",
            SubtitleHearingImpairedPreferences.PlainPreferred, CancellationToken.None);

        Assert.False(outcome.Found);
        var health = Assert.Single(repository.Health);
        Assert.False(health.Success);
        Assert.Equal(DateTimeOffset.Parse("2026-04-30T00:00:00Z"), health.RateLimitedUntil);
        Assert.Equal(IntegrationFailureKind.QuotaExceeded, Assert.Single(repository.TypedFailures)!.Kind);

        var recorded = Assert.Single(activity.Recorded);
        Assert.Equal("subtitle.provider.paused", recorded.Category);
        Assert.Contains("first", recorded.Message, StringComparison.Ordinal);
        Assert.Equal("subtitle-provider", recorded.RelatedEntityType);
        Assert.Equal("first", recorded.RelatedEntityId);
    }

    /// <summary>
    /// The "accepted but no link" shape (#946) — HTTP 200, no reset time in
    /// the body — falls back to the next UTC midnight rather than leaving the
    /// provider with no resume time at all.
    /// </summary>
    [Fact]
    public async Task A_quota_exhaustion_without_a_reset_time_still_gets_a_resume_time()
    {
        var provider = new FakeProvider("first", [Candidate("en")], Srt)
        {
            ThrownDownloadException = new SubtitleProviderQuotaExhaustedException(
                "first", "Accepted the request but did not return a link.")
        };
        var repository = new FakeRepository([Connection("first")]);
        var service = Build([provider], new FakeWriter(), repository);

        await service.FetchAsync(
            Request(), "en", false, @"D:\Media\Dune\Dune.mkv",
            SubtitleHearingImpairedPreferences.PlainPreferred, CancellationToken.None);

        var health = Assert.Single(repository.Health);
        Assert.NotNull(health.RateLimitedUntil);
    }

    [Fact]
    public async Task A_tv_only_provider_is_not_asked_about_a_film()
    {
        // Gestdown returns nothing for a film and Yify nothing for an episode.
        // Asking anyway and counting the empty answer as a failure would mark
        // two working sources unhealthy on every cycle.
        var tvOnly = new FakeProvider("first", [Candidate("en")], Srt) { Scope = SubtitleProviderScope.TvOnly };
        var service = Build([tvOnly], new FakeWriter());

        var outcome = await service.FetchAsync(Request(), "en", isEpisodeMedia: false, @"D:\Media\Dune\Dune.mkv", SubtitleHearingImpairedPreferences.PlainPreferred, CancellationToken.None);

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

        var outcome = await service.FetchAsync(Request(), "en", false, @"D:\Media\Dune\Dune.mkv", SubtitleHearingImpairedPreferences.PlainPreferred, CancellationToken.None);

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

        var outcome = await service.FetchAsync(Request(), "en", false, @"D:\Media\Dune\Dune.mkv", SubtitleHearingImpairedPreferences.PlainPreferred, CancellationToken.None);

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

    /// <summary>#841: manual search shows every ranked candidate rather than stopping at the first.</summary>
    [Fact]
    public async Task SearchCandidatesAsync_lists_every_provider_ranked_rather_than_stopping_at_the_first()
    {
        var first = new FakeProvider("first", [Candidate("en")], Srt);
        var second = new FakeProvider("second", [Candidate("en") with { DownloadToken = "other" }], Srt);
        var service = Build([first, second], new FakeWriter());

        var candidates = await service.SearchCandidatesAsync(
            Request(), "en", false, @"D:\Media\Dune\Dune.mkv", false, CancellationToken.None);

        Assert.Equal(2, candidates.Count);
        Assert.Equal(1, first.Searches);
        Assert.Equal(1, second.Searches);
        Assert.Contains(candidates, c => c.ProviderKey == "first" && c.DownloadToken == "token");
        Assert.Contains(candidates, c => c.ProviderKey == "second" && c.DownloadToken == "other");
    }

    [Fact]
    public async Task SearchCandidatesAsync_never_downloads_anything()
    {
        var provider = new FakeProvider("first", [Candidate("en")], Srt);
        var writer = new FakeWriter();
        var service = Build([provider], writer);

        await service.SearchCandidatesAsync(Request(), "en", false, @"D:\Media\Dune\Dune.mkv", false, CancellationToken.None);

        Assert.Null(provider.LastDownloadToken);
        Assert.Empty(writer.Written);
    }

    [Fact]
    public async Task SearchCandidatesAsync_excludes_blocklisted_candidates()
    {
        var provider = new FakeProvider(
            "first",
            [Candidate("en"), Candidate("en") with { DownloadToken = "keep" }],
            Srt);
        var service = Build([provider], new FakeWriter());

        var candidates = await service.SearchCandidatesAsync(
            Request(), "en", false, @"D:\Media\Dune\Dune.mkv", false, CancellationToken.None,
            blockedKeys: [SubtitleBlockKeys.For("first", "token")]);

        var candidate = Assert.Single(candidates);
        Assert.Equal("keep", candidate.DownloadToken);
    }

    /// <summary>#841: picking a candidate downloads and writes exactly that one, through the same write path a fetch uses.</summary>
    [Fact]
    public async Task DownloadCandidateAsync_writes_the_chosen_candidate()
    {
        var provider = new FakeProvider(
            "first",
            [Candidate("en"), Candidate("en") with { DownloadToken = "other" }],
            Srt);
        var writer = new FakeWriter();
        var service = Build([provider], writer);

        var outcome = await service.DownloadCandidateAsync(
            Request(), "first", "other", "en", false, @"D:\Media\Dune\Dune.mkv", CancellationToken.None);

        Assert.True(outcome.Found);
        Assert.Equal("other", provider.LastDownloadToken);
        Assert.Single(writer.Written);
    }

    [Fact]
    public async Task DownloadCandidateAsync_fails_honestly_when_the_candidate_is_gone()
    {
        var provider = new FakeProvider("first", [Candidate("en")], Srt);
        var service = Build([provider], new FakeWriter());

        var outcome = await service.DownloadCandidateAsync(
            Request(), "first", "vanished", "en", false, @"D:\Media\Dune\Dune.mkv", CancellationToken.None);

        Assert.False(outcome.Found);
        Assert.Contains("no longer offers", outcome.Reason, StringComparison.Ordinal);
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
            SubtitleHearingImpairedPreferences.PlainPreferred,
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
            SubtitleHearingImpairedPreferences.PlainPreferred,
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
            SubtitleHearingImpairedPreferences.PlainPreferred,
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

    [Fact]
    public void A_forced_track_never_overwrites_the_full_subtitle_for_the_same_language()
        => Assert.Equal(
            "Dune (2021).en.forced.srt",
            SubtitleFileNaming.For(@"D:\Media\Dune\Dune (2021).mkv", "en", hearingImpaired: false, forced: true));

    /// <summary>
    /// The same video is given the same subtitle name wherever Deluno runs.
    ///
    /// <para><b>This is the test CI found and Windows never could.</b> Every
    /// fixture here writes a Windows path, and on Windows <c>Path</c> treats
    /// both separators, so the naming rule looked right for as long as nobody
    /// ran it anywhere else. On Linux a backslash is an ordinary filename
    /// character: the whole path came back as the "file name", and Deluno
    /// would have written a subtitle called
    /// <c>D:\Media\Dune\Dune (2021).en.srt</c> - in the container image,
    /// which is the Linux one.</para>
    ///
    /// <para>Which host recorded the path is not knowable from the path, and
    /// need not be: the paths a migration from Radarr carries are whatever that
    /// install wrote. Both shapes, asserted together, so this cannot pass on
    /// one platform by accident again.</para>
    /// </summary>
    [Theory]
    [InlineData(@"D:\Media\Dune\Dune (2021).mkv")]
    [InlineData("/media/Dune/Dune (2021).mkv")]
    [InlineData("Dune (2021).mkv")]
    public void A_subtitle_is_named_the_same_wherever_Deluno_is_running(string videoPath)
        => Assert.Equal("Dune (2021).en.srt", SubtitleFileNaming.For(videoPath, "en", hearingImpaired: false));

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

    [Fact]
    public async Task A_provider_whose_saved_key_cannot_be_read_here_is_not_asked_and_says_why()
    {
        // #1094: the same sentence the screen shows, not a crypto exception.
        var provider = new FakeProvider("first", [], Srt);
        var unreadable = Connection("first") with { };
        unreadable = new SubtitleProviderConnection(
            unreadable.Id, unreadable.ProviderKey, unreadable.Name, null, null, null, 100, true, "degraded",
            null, null, null, 0, null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)
        {
            SecretUnreadable = true,
            SecretProblem = Deluno.Security.SecretUnreadable.Message("API key")
        };
        var service = Build([provider], new FakeWriter(), new FakeRepository([unreadable]));

        var outcome = await service.FetchAsync(
            Request(), "en", false, @"D:\Media\Dune\Dune.mkv",
            SubtitleHearingImpairedPreferences.PlainPreferred, CancellationToken.None);

        Assert.False(outcome.Found);
        Assert.Contains("can't be read here. Enter it again.", outcome.Reason, StringComparison.Ordinal);
        Assert.False(unreadable.IsAskable(DateTimeOffset.UtcNow));
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
        public Exception? ThrownException { get; set; }
        public Exception? ThrownDownloadException { get; set; }
        public int Searches { get; private set; }
        public string? LastDownloadToken { get; private set; }

        /// <summary>Overrides <c>payload</c> for one candidate's token, so a test can make one candidate bad and another good.</summary>
        public Dictionary<string, byte[]> PayloadByToken { get; } = [];

        public Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(
            SubtitleSearchRequest request, SubtitleProviderCredentials credentials, CancellationToken cancellationToken)
        {
            Searches++;
            if (RateLimited) throw new SubtitleProviderRateLimitedException(key, TimeSpan.FromMinutes(5));
            if (Unreachable) throw new HttpRequestException($"{key} could not be resolved.");
            if (ThrownException is not null) throw ThrownException;
            return Task.FromResult(results);
        }

        public Task<byte[]> DownloadAsync(
            SubtitleCandidate candidate, SubtitleProviderCredentials credentials, CancellationToken cancellationToken)
        {
            if (ThrownDownloadException is not null) throw ThrownDownloadException;
            LastDownloadToken = candidate.DownloadToken;
            return Task.FromResult(PayloadByToken.TryGetValue(candidate.DownloadToken, out var specific) ? specific : payload);
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
            bool omitLanguageCode = false,
            bool forced = false)
        {
            // Through the real naming rule rather than a second copy of it, so
            // this fake cannot quietly disagree with what lands on disk.
            var path = Path.Combine(
                Path.GetDirectoryName(videoPath)!,
                SubtitleFileNaming.For(videoPath, language, hearingImpaired, omitLanguageCode, forced));

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

    private sealed class RecordingActivityFeedRepository : IActivityFeedRepository
    {
        public List<(string Category, string Message, string? RelatedEntityType, string? RelatedEntityId)> Recorded { get; } = [];

        public List<string?> Details { get; } = [];

        public Task<IReadOnlyList<ActivityEventItem>> ListActivityAsync(
            int take, string? relatedEntityType, string? relatedEntityId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<Page<ActivityEventItem>> ListActivityPageAsync(
            PageRequest request, string? relatedEntityType, string? relatedEntityId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ActivityEventItem> RecordActivityAsync(
            string category, string message, string? detailsJson, string? relatedJobId,
            string? relatedEntityType, string? relatedEntityId, CancellationToken cancellationToken)
        {
            Recorded.Add((category, message, relatedEntityType, relatedEntityId));
            Details.Add(detailsJson);
            return Task.FromResult(new ActivityEventItem(
                Id: Guid.NewGuid().ToString("n"), Category: category, Message: message, DetailsJson: detailsJson,
                RelatedJobId: relatedJobId, RelatedEntityType: relatedEntityType, RelatedEntityId: relatedEntityId,
                CreatedUtc: DateTimeOffset.UtcNow));
        }

        public Task<IReadOnlyList<ActivityEventItem>> RecordActivityBatchAsync(
            IReadOnlyList<ActivityEventItem> activities, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<int> ClearActivityAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ActivityStatsSnapshot> GetActivityStatsAsync(DateTimeOffset sinceUtc, int windowHours, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
