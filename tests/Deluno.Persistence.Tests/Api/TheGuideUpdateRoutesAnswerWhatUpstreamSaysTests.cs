using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;
using Deluno.Quality.Guides;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The guide update routes (coverage register, area api): the weekly-check switch, "Check now" and "Preview sync", run through the whole
/// application with the TRaSH repository answered by a stand-in that serves the active package's own pinned file list (the repository
/// is public and its archive far too large to keep as a recording). A switched-off check never makes a request; a changed file is
/// reported, not applied; and a preview built on a stale check is refused in plain words and changes nothing.
/// </summary>
public sealed class TheGuideUpdateRoutesAnswerWhatUpstreamSaysTests
{
    private sealed class TrashRepository(GuideSourceInventory source, string? changedPath = null, string revision = "remote-guide-revision") : HttpMessageHandler
    {
        public int ApiRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host != "api.github.com")
            {
                // The free route is tried first; the repository's address is not answered here, so the API route is used.
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            ApiRequests++;
            var path = request.RequestUri.PathAndQuery;
            object response = path switch
            {
                "/repos/TRaSH-Guides/Guides/git/ref/heads/master" => new { @object = new { sha = revision } },
                var commit when commit == $"/repos/TRaSH-Guides/Guides/git/commits/{revision}" => new { tree = new { sha = "remote-guide-tree" } },
                "/repos/TRaSH-Guides/Guides/git/trees/remote-guide-tree?recursive=1" => new { truncated = false, tree = Tree() },
                _ => throw new InvalidOperationException($"Unexpected request: {path}")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(response), Encoding.UTF8, "application/json") });
        }

        private object[] Tree()
            => source.CustomFormats.Select(format => (format.SourcePath, format.SourceBlobSha))
                .Concat(source.FormatGroups.Select(group => (group.SourcePath, group.SourceBlobSha)))
                .Concat(source.QualityProfiles.Select(profile => (profile.SourcePath, profile.SourceBlobSha)))
                .DistinctBy(item => item.SourcePath, StringComparer.Ordinal)
                .Select(item => (object)new { path = item.SourcePath, type = "blob", sha = item.SourcePath == changedPath ? "different-remote-blob" : item.SourceBlobSha })
                .ToArray();
    }

    private static Task<ApplicationTestHost> StartAsync(TrashRepository repository)
        => ApplicationTestHost.StartAsync(replaceServices: services =>
            services.AddHttpClient(GuideUpstreamTreeClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => repository));

    [Fact]
    public async Task The_weekly_check_is_off_until_switched_on_and_a_check_now_while_it_is_off_asks_nobody()
    {
        var repository = new TrashRepository(GuidePackageCatalog.Current.SourceInventory!);
        await using var app = await StartAsync(repository);
        var state = await app.Client.GetFromJsonAsync<JsonElement>("/api/v1/guides/trash/update-check");
        Assert.False(state.GetProperty("isEnabled").GetBoolean());

        var run = await app.Client.PostAsync("/api/v1/guides/trash/update-check/run", content: null);

        Assert.Equal(HttpStatusCode.OK, run.StatusCode);
        Assert.Equal("disabled", (await run.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        Assert.Equal(0, repository.ApiRequests);

        var on = await app.Client.PutAsJsonAsync("/api/v1/guides/trash/update-check/settings", new { isEnabled = true });
        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        Assert.True((await app.Client.GetFromJsonAsync<JsonElement>("/api/v1/guides/trash/update-check")).GetProperty("isEnabled").GetBoolean());

        var off = await app.Client.PutAsJsonAsync("/api/v1/guides/trash/update-check/settings", new { isEnabled = false });
        Assert.False((await off.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("isEnabled").GetBoolean());
        Assert.False((await app.Client.GetFromJsonAsync<JsonElement>("/api/v1/guides/trash/update-check")).GetProperty("isEnabled").GetBoolean());
    }

    [Fact]
    public async Task Check_now_with_the_check_on_reports_a_changed_file_and_applies_nothing_and_an_unchanged_repository_is_up_to_date()
    {
        var source = GuidePackageCatalog.Current.SourceInventory!;
        var changed = source.CustomFormats.First(format => format.MediaType == "movies");
        var repository = new TrashRepository(source, changed.SourcePath);
        await using var app = await StartAsync(repository);
        await app.Client.PutAsJsonAsync("/api/v1/guides/trash/update-check/settings", new { isEnabled = true });
        var versionsBefore = (await app.Client.GetFromJsonAsync<JsonElement>("/api/v1/guides/trash/versions")).GetArrayLength();

        var run = await app.Client.PostAsync("/api/v1/guides/trash/update-check/run", content: null);

        Assert.Equal(HttpStatusCode.OK, run.StatusCode);
        var state = await run.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("update-available", state.GetProperty("status").GetString());
        Assert.Equal("remote-guide-revision", state.GetProperty("report").GetProperty("remoteRevision").GetString());
        var change = state.GetProperty("report").GetProperty("changes").EnumerateArray().Single(item => item.GetProperty("sourcePath").GetString() == changed.SourcePath);
        Assert.Equal("changed", change.GetProperty("changeType").GetString());
        Assert.Equal(versionsBefore, (await app.Client.GetFromJsonAsync<JsonElement>("/api/v1/guides/trash/versions")).GetArrayLength());
        Assert.Equal("update-available", (await app.Client.GetFromJsonAsync<JsonElement>("/api/v1/guides/trash/update-check")).GetProperty("status").GetString());

        var quiet = new TrashRepository(source);
        await using var calm = await StartAsync(quiet);
        await calm.Client.PutAsJsonAsync("/api/v1/guides/trash/update-check/settings", new { isEnabled = true });
        var same = await (await calm.Client.PostAsync("/api/v1/guides/trash/update-check/run", content: null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("up-to-date", same.GetProperty("status").GetString());
        Assert.Empty(same.GetProperty("report").GetProperty("changes").EnumerateArray());
    }

    [Fact]
    public async Task A_sync_preview_built_on_a_stale_check_is_refused_in_plain_words_and_changes_nothing()
    {
        var source = GuidePackageCatalog.Current.SourceInventory!;
        await using var app = await StartAsync(new TrashRepository(source));
        var versions = await app.Client.GetFromJsonAsync<JsonElement>("/api/v1/guides/trash/versions");
        var versionsBefore = versions.GetArrayLength();
        var current = versions.EnumerateArray().Last().GetProperty("integritySha256").GetString();

        var stalePackage = await app.Client.PostAsJsonAsync("/api/v1/guides/trash/sync/preview", new { expectedCurrentIntegritySha256 = "not-the-active-package", expectedUpstreamRevision = "remote-guide-revision" });
        var staleUpstream = await app.Client.PostAsJsonAsync("/api/v1/guides/trash/sync/preview", new { expectedCurrentIntegritySha256 = current, expectedUpstreamRevision = "an-older-revision" });

        foreach (var (response, message) in new[]
        {
            (stalePackage, "The active guide package changed. Check for guide updates again before syncing."),
            (staleUpstream, "TRaSH Guides changed after this check. Check again, then review the new sync preview.")
        })
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var preview = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.False(preview.GetProperty("canApply").GetBoolean());
            Assert.Contains(message, preview.ToString());
        }

        Assert.Equal(versionsBefore, (await app.Client.GetFromJsonAsync<JsonElement>("/api/v1/guides/trash/versions")).GetArrayLength());
    }
}
