using System.Net;
using System.Text;
using Deluno.Companions;
using Deluno.Contracts;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Quality.ReleasePreferences;
using Deluno.Security.Contracts;

namespace Deluno.Platform.Tests.Contracts;

/// <summary>
/// The release walk the tray and the update tool share (newest release that has its files), and the
/// small contract types around it: colon handling, page tokens, the allowed-hosts records, Weir's
/// connect outcome and the issue-to-handoff link.
/// </summary>
public sealed class ReleaseFallbackAndSmallContractsTests
{
    private const string Repo = "owner/app";

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpStatusCode> status, Func<HttpRequestMessage, string>? body = null) : HttpMessageHandler
    {
        public List<string> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add($"{request.Method} {request.RequestUri!.Segments[^1]}");
            return Task.FromResult(new HttpResponseMessage(status(request))
            {
                Content = new StringContent(body?.Invoke(request) ?? string.Empty, Encoding.UTF8)
            });
        }
    }

    [Fact]
    public void The_plain_sentences_name_the_release_that_is_missing_its_files()
    {
        Assert.Equal("v1's installer download was not there yet", ReleaseFallback.InstallerNotThere("v1"));
        Assert.Equal(
            "Weir's newest releases have no Windows installer yet; try again in a few minutes.",
            ReleaseFallback.NoInstallerYetSentence("Weir", null));
        Assert.Equal(
            "Weir's newest releases have no Windows installer yet (the newest, v3, does not have all its files); try again in a few minutes.",
            ReleaseFallback.NoInstallerYetSentence("Weir", "v3"));
    }

    [Fact]
    public void An_attempt_is_found_skipped_or_unreadable()
    {
        var found = ReleaseAttempt<string>.Found("x");
        Assert.Equal(("x", null, false), (found.Value, found.SkipReason, found.Unreadable));
        var skip = ReleaseAttempt<string>.Skip("why");
        Assert.Equal((null, "why", false), (skip.Value, skip.SkipReason, skip.Unreadable));
        Assert.True(ReleaseAttempt<string>.CouldNotRead().Unreadable);
    }

    [Fact]
    public async Task The_walk_takes_the_newest_release_that_has_its_files_and_remembers_why_newer_ones_were_passed_over()
    {
        var tried = new List<string>();
        var walk = await ReleaseFallback.WalkAsync<string>(
            ["v4", "v3", "v2", "v1"],
            ["v4"],
            tag =>
            {
                tried.Add(tag);
                return Task.FromResult(tag == "v2" ? ReleaseAttempt<string>.Found("files") : tag == "v3" ? ReleaseAttempt<string>.Skip("v3 has no sums") : ReleaseAttempt<string>.Skip(null!));
            });

        Assert.True(walk.Found);
        Assert.Equal(("files", "v2", "v4", false), (walk.Value, walk.Tag, walk.NewestLooked, walk.Unreadable));
        Assert.Equal(["v4's installer download was not there yet", "v3 has no sums"], walk.Skipped);
        Assert.Equal(["v3", "v2"], tried);
    }

    [Fact]
    public async Task The_walk_stops_when_a_release_cannot_be_read_and_gives_up_after_the_cap()
    {
        var unreadable = await ReleaseFallback.WalkAsync<string>(["v2", "v1"], null, _ => Task.FromResult(ReleaseAttempt<string>.CouldNotRead()));
        Assert.True(unreadable.Unreadable);
        Assert.False(unreadable.Found);

        var tried = 0;
        var none = await ReleaseFallback.WalkAsync<string>(
            ["a", "b", "c", "d"], null,
            _ =>
            {
                tried++;
                return Task.FromResult(new ReleaseAttempt<string>(null, null, false));
            },
            maxReleases: 2);
        Assert.False(none.Found);
        Assert.False(none.Unreadable);
        Assert.Equal(2, tried);
        Assert.Equal(["a has no files yet", "b has no files yet"], none.Skipped);
        Assert.Equal("a", none.NewestLooked);

        var empty = await ReleaseFallback.WalkAsync<string>([], null, _ => throw new InvalidOperationException());
        Assert.Null(empty.NewestLooked);
        Assert.Equal(5, ReleaseFallback.MaxReleasesLooked);
    }

    private static string? ParseSums(string text, string asset) => text.Contains(asset) ? "abc123" : null;

    [Fact]
    public async Task A_missing_checksum_file_passes_the_release_over_and_a_failing_one_cannot_be_read()
    {
        using var notFound = new HttpClient(new ScriptedHandler(_ => HttpStatusCode.NotFound));
        var skip = await ReleaseFallback.TryDownloadFolderAsync(notFound, Repo, "v1", "setup.exe", "sums.txt", ParseSums, false, default);
        Assert.Equal("v1 has no sums.txt yet", skip.SkipReason);

        using var broken = new HttpClient(new ScriptedHandler(_ => HttpStatusCode.InternalServerError));
        Assert.True((await ReleaseFallback.TryDownloadFolderAsync(broken, Repo, "v1", "setup.exe", "sums.txt", ParseSums, false, default)).Unreadable);
    }

    [Fact]
    public async Task A_checksum_file_that_does_not_list_the_installer_passes_the_release_over()
    {
        using var http = new HttpClient(new ScriptedHandler(_ => HttpStatusCode.OK, _ => "nothing here"));
        var attempt = await ReleaseFallback.TryDownloadFolderAsync(http, Repo, "v1", "setup.exe", "sums.txt", ParseSums, false, default);
        Assert.Equal("v1's sums.txt does not list setup.exe", attempt.SkipReason);
    }

    [Fact]
    public async Task A_listed_installer_is_found_with_its_checksum_and_is_probed_only_when_asked()
    {
        var handler = new ScriptedHandler(_ => HttpStatusCode.OK, _ => "abc setup.exe");
        using var http = new HttpClient(handler);
        var plain = await ReleaseFallback.TryDownloadFolderAsync(http, Repo, "v1.0", "setup.exe", "sums.txt", ParseSums, false, default);
        Assert.Equal(new ReleaseFiles("https://github.com/owner/app/releases/download/v1.0/setup.exe", "abc123"), plain.Value);
        Assert.Equal(["GET sums.txt"], handler.Seen);

        handler.Seen.Clear();
        var probed = await ReleaseFallback.TryDownloadFolderAsync(http, Repo, "v1.0", "setup.exe", "sums.txt", ParseSums, true, default);
        Assert.Equal("abc123", probed.Value!.Sha256);
        Assert.Equal(["GET sums.txt", "HEAD setup.exe"], handler.Seen);
    }

    [Fact]
    public async Task An_installer_that_is_not_there_yet_after_its_checksum_passes_the_release_over()
    {
        using var http = new HttpClient(new ScriptedHandler(r => r.Method == HttpMethod.Head ? HttpStatusCode.NotFound : HttpStatusCode.OK, _ => "abc setup.exe"));
        var attempt = await ReleaseFallback.TryDownloadFolderAsync(http, Repo, "v1", "setup.exe", "sums.txt", ParseSums, true, default);
        Assert.Equal("v1's installer download was not there yet", attempt.SkipReason);
    }

    [Fact]
    public async Task A_component_with_no_checksum_is_judged_by_a_head_on_its_installer()
    {
        using var ok = new HttpClient(new ScriptedHandler(_ => HttpStatusCode.OK));
        var found = await ReleaseFallback.TryDownloadFolderAsync(ok, Repo, "v1", "setup.exe", null, ParseSums, false, default);
        Assert.Null(found.Value!.Sha256);

        using var blank = new HttpClient(new ScriptedHandler(_ => HttpStatusCode.OK));
        Assert.NotNull((await ReleaseFallback.TryDownloadFolderAsync(blank, Repo, "v1", "setup.exe", "  ", ParseSums, false, default)).Value);

        using var missing = new HttpClient(new ScriptedHandler(_ => HttpStatusCode.NotFound));
        Assert.Equal("v1 has no setup.exe yet", (await ReleaseFallback.TryDownloadFolderAsync(missing, Repo, "v1", "setup.exe", null, ParseSums, false, default)).SkipReason);

        using var failing = new HttpClient(new ScriptedHandler(_ => HttpStatusCode.BadGateway));
        Assert.True((await ReleaseFallback.TryDownloadFolderAsync(failing, Repo, "v1", "setup.exe", null, ParseSums, false, default)).Unreadable);
    }

    [Theory]
    [InlineData("delete", "delete")]
    [InlineData(" DASH ", "dash")]
    [InlineData("space-dash", "space-dash")]
    [InlineData("Space-Dash-Space", "space-dash-space")]
    [InlineData("smart", "smart")]
    [InlineData("nonsense", "smart")]
    [InlineData(null, "smart")]
    public void A_colon_mode_is_normalised_to_a_known_one(string? value, string expected)
        => Assert.Equal(expected, ColonReplacementModes.Normalize(value));

    [Theory]
    [InlineData("dash", true)]
    [InlineData(" SMART ", true)]
    [InlineData("space-dash-space", true)]
    [InlineData("other", false)]
    [InlineData(null, false)]
    public void Only_the_five_modes_are_known(string? value, bool known)
        => Assert.Equal(known, ColonReplacementModes.IsKnown(value));

    [Fact]
    public void A_page_request_is_bounded_and_a_page_knows_if_there_is_more()
    {
        Assert.Equal(50, new PageRequest().BoundedPageSize);
        Assert.Equal(50, new PageRequest(0).BoundedPageSize);
        Assert.Equal(50, new PageRequest(-3).BoundedPageSize);
        Assert.Equal(7, new PageRequest(7).BoundedPageSize);
        Assert.Equal(PageRequest.MaximumPageSize, new PageRequest(100_000).BoundedPageSize);
        Assert.True(Page<int>.Of([1], "next").HasMore);
        Assert.False(Page<int>.Of([1], null).HasMore);
        Assert.False(Page<int>.Of([1], "").HasMore);
    }

    [Fact]
    public void A_page_token_round_trips_and_a_bad_one_restarts_at_the_first_page()
    {
        var token = DelunoPageToken.Encode("2026-10-31", null, "id-9");
        Assert.Equal(new string?[] { "2026-10-31", null, "id-9" }, DelunoPageToken.Decode(token, 3)!.AsEnumerable());
        Assert.Null(DelunoPageToken.Decode(token, 2));
        Assert.Null(DelunoPageToken.Decode(null, 3));
        Assert.Null(DelunoPageToken.Decode("  ", 3));
        Assert.Null(DelunoPageToken.Decode("not base64!", 3));
        Assert.Null(DelunoPageToken.Decode(Convert.ToBase64String(Encoding.UTF8.GetBytes("{not json")), 3));
        Assert.Null(DelunoPageToken.Decode(Convert.ToBase64String(Encoding.UTF8.GetBytes("null")), 3));
        Assert.Null(DelunoPageToken.Decode(Convert.ToBase64String([0xFF, 0xFE, 0xFD]), 3));
    }

    [Fact]
    public void The_allowed_hosts_records_keep_what_they_are_given()
    {
        Assert.False(AllowedHostsSettings.Off.Enabled);
        Assert.Empty(AllowedHostsSettings.Off.AllowedHosts);
        var candidate = new DetectedHostCandidate("deluno.local", "this computer's name");
        var overview = new AllowedHostsOverview(true, ["a"], [candidate], "a");
        Assert.Equal((true, "a"), (overview.Enabled, overview.CurrentHost));
        Assert.Same(candidate, overview.DetectedHosts[0]);
        Assert.Equal(("deluno.local", "this computer's name"), (candidate.Host, candidate.Reason));
        var update = new UpdateAllowedHostsRequest(true, ["a"]);
        Assert.True(update.Enabled);
        Assert.Equal(["a"], update.AllowedHosts);
    }

    [Fact]
    public void Weir_contract_types_keep_their_values_and_the_default_login_keeper_keeps_nothing()
    {
        var outcome = new WeirConnectOutcome(true, "ok", false);
        Assert.Null(outcome.ProcessorConnectionId);
        Assert.Null(outcome.ProcessorConnectionName);
        var named = new WeirConnectOutcome(false, "no", true, "p-1", "Weir");
        Assert.Equal(("p-1", "Weir", true), (named.ProcessorConnectionId, named.ProcessorConnectionName, named.NeedsSignIn));
        var state = new WeirAccountState(true, false, true);
        Assert.Equal((true, false, true), (state.Installed, state.NeedsAccount, state.AccountExists));
        Assert.Null(new CompanionInstallRequest(null).ToolIds);
        new NoInstalledClientLogins().Keep("qbittorrent", "user", "secret");
    }

    [Fact]
    public void An_import_issue_names_its_handoff_from_the_details_the_watcher_wrote()
    {
        Assert.Equal("processor-output-issue:case-1", ProblemTwins.OutputIssueRowId("case-1"));
        Assert.Equal("h-1", ProblemTwins.HandoffIdIn("{\"handoffId\":\"h-1\"}"));
        Assert.Equal("h-2", ProblemTwins.HandoffIdIn("{\"other\":1,\"HandoffId\":\"h-2\"}"));
        Assert.Null(ProblemTwins.HandoffIdIn(null));
        Assert.Null(ProblemTwins.HandoffIdIn("  "));
        Assert.Null(ProblemTwins.HandoffIdIn("[1,2]"));
        Assert.Null(ProblemTwins.HandoffIdIn("{\"handoffId\":5}"));
        Assert.Null(ProblemTwins.HandoffIdIn("{\"handoffId\":\" \"}"));
        Assert.Null(ProblemTwins.HandoffIdIn("{\"nothing\":\"x\"}"));
        Assert.Null(ProblemTwins.HandoffIdIn("{broken"));
    }

    [Fact]
    public void The_release_preference_api_writes_enums_as_camel_case_names()
    {
        Assert.Equal("\"deleted\"", System.Text.Json.JsonSerializer.Serialize(DayOfWeekLike.Deleted, ReleasePreferenceJson.Options));
        var compilation = new ReleasePreferencePlanCompilation("r1", "p1", "Profile", null!, "hash", [], ["warn"], true, DateTimeOffset.Parse("2026-10-31T00:00:00Z"));
        Assert.Equal(("r1", "p1", "Profile", "hash", true), (compilation.RegistryVersion, compilation.ProfileId, compilation.ProfileName, compilation.PlanHash, compilation.RequiresReview));
        Assert.Equal(["warn"], compilation.Warnings);
        Assert.Empty(compilation.AdvancedRules);
    }

    private enum DayOfWeekLike { Deleted }
}
