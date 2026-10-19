using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Connections.Versions;
using Deluno.Contracts;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// "A newer version of qBittorrent is out" (#1118): the version comes from a test of the connection against what a real qBittorrent 5.2.1
/// answered (tests/fixtures/recorded/qbittorrent), the newest release from GitHub's own tag list for qBittorrent (tests/fixtures/recorded/github,
/// read by the same parser the feed uses). Dismissing the notice keeps it away until a still newer version comes out.
/// </summary>
public sealed class ANewerVersionNoticeCanBeDismissedTests
{
    private sealed class NewestFromTheRecordedTags : ILatestReleaseService
    {
        private readonly LatestRelease _release = ReleaseFeedParsers.FromGitHubTags(
            RecordedReplies.Github("qbittorrent-tags").Find("api.github.com").Reply.Body!, "qbittorrent/qBittorrent", "release-")!;

        public LatestRelease Release => _release;

        public LatestRelease? Peek(string project) => project == ConnectionProjects.Qbittorrent ? _release : null;
    }

    [Fact]
    public async Task Dismissing_the_notice_for_a_connection_keeps_it_away_and_names_the_version_it_was_dismissed_for()
    {
        var newest = new NewestFromTheRecordedTags();
        await using var qbit = await RecordedAppServer.StartAsync("qbittorrent", new[] { "sign-in", "version", "settings", "idle" }.Select(RecordedReplies.Qbittorrent));
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services =>
        {
            services.RemoveAll<ILatestReleaseService>();
            services.AddSingleton<ILatestReleaseService>(newest);
        });
        var created = await app.Client.PostAsJsonAsync("/api/download-clients", new
        {
            name = "qBittorrent", protocol = "qbittorrent", host = qbit.Host, port = qbit.Port, username = "deluno", password = "a-test-password",
            moviesCategory = "movies", tvCategory = "tv", priority = 10, isEnabled = true
        });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var tested = await app.Client.PostAsync($"/api/connections/downloadClient/{id}/test", content: null);
        Assert.Equal(HttpStatusCode.OK, tested.StatusCode);
        Assert.True((await tested.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ok").GetBoolean());

        var before = Assert.Single((await app.Client.GetFromJsonAsync<JsonElement>("/api/connections/updates")).EnumerateArray());
        Assert.Equal("5.2.1", before.GetProperty("version").GetString());
        Assert.Equal(newest.Release.Version, before.GetProperty("latestVersion").GetString());
        Assert.True(before.GetProperty("notice").GetBoolean());

        var dismissed = await app.Client.PostAsync($"/api/connections/downloadClient/{id}/version/dismiss", content: null);

        Assert.Equal(HttpStatusCode.OK, dismissed.StatusCode);
        Assert.Equal(newest.Release.Version, (await dismissed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("dismissed").GetString());
        var after = Assert.Single((await app.Client.GetFromJsonAsync<JsonElement>("/api/connections/updates")).EnumerateArray());
        Assert.False(after.GetProperty("notice").GetBoolean());
        Assert.Equal(newest.Release.Version, after.GetProperty("dismissedVersion").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsync("/api/connections/downloadClient/no-such-connection/version/dismiss", content: null)).StatusCode);
    }
}
