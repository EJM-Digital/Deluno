using System.Net;
using System.Text;
using System.Text.Json;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Quality.Contracts;
using Deluno.Quality.Data;
using Deluno.Quality.Guides;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Deluno.Persistence.Tests.Quality;

public sealed class GuideUpdateCheckServiceTests
{
    [Fact]
    public async Task Disabled_update_check_never_makes_a_network_request()
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-02T00:00:00Z"));
        await InitializePlatformAsync(storage, time);
        var handler = new FixedGitHubHandler(GuidePackageCatalog.Current.SourceInventory!, null);
        var service = CreateService(storage, time, handler);

        var state = await service.CheckNowAsync(CancellationToken.None);

        Assert.False(state.IsEnabled);
        Assert.Equal(GuideUpdateCheckStatuses.Disabled, state.Status);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task Enabled_check_reports_changed_used_source_and_added_candidate_without_applying_it()
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-02T00:00:00Z"));
        await InitializePlatformAsync(storage, time);
        var source = GuidePackageCatalog.Current.SourceInventory!;
        var changed = source.CustomFormats.First(format => format.MediaType == "movies");
        var handler = new FixedGitHubHandler(source, changed.SourcePath, includeAddedSource: true);
        var service = CreateService(storage, time, handler);
        var quality = new SqliteQualityRepository(storage.Factory, time);
        var saved = await quality.CreateCustomFormatAsync(
            new CreateCustomFormatRequest(
                "Saved source format",
                "movies",
                100,
                changed.TrashId,
                "",
                true),
            CancellationToken.None);

        await service.SetEnabledAsync(true, CancellationToken.None);
        var state = await service.CheckNowAsync(CancellationToken.None);

        Assert.Equal(GuideUpdateCheckStatuses.UpdateAvailable, state.Status);
        Assert.NotNull(state.Report);
        Assert.Equal("remote-guide-revision", state.Report.RemoteRevision);
        var updated = Assert.Single(state.Report.Changes, change => change.SourcePath == changed.SourcePath);
        Assert.Equal("custom-format", updated.Kind);
        Assert.Equal("changed", updated.ChangeType);
        Assert.True(updated.IsInUse);
        Assert.Contains(saved.Id, updated.InUseCustomFormatIds);
        var added = Assert.Single(state.Report.AddedSources);
        Assert.Equal("custom-format", added.Kind);
        Assert.Equal("movies", added.MediaType);
        Assert.Equal("docs/json/radarr/cf/new-upstream-format.json", added.SourcePath);

        var package = await new SqliteGuidePackageStore(storage.Factory, time).GetCurrentAsync(CancellationToken.None);
        Assert.Equal(GuidePackageCatalog.Current.IntegritySha256, package.IntegritySha256);
        Assert.Equal(3, handler.RequestCount);
    }

    [Fact]
    public async Task Due_check_runs_once_a_week_and_is_noop_before_the_interval()
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-02T00:00:00Z"));
        await InitializePlatformAsync(storage, time);
        var handler = new FixedGitHubHandler(GuidePackageCatalog.Current.SourceInventory!, null);
        var service = CreateService(storage, time, handler);

        await service.SetEnabledAsync(true, CancellationToken.None);
        var first = await service.RunIfDueAsync(CancellationToken.None);
        var second = await service.RunIfDueAsync(CancellationToken.None);

        Assert.Equal(GuideUpdateCheckStatuses.UpToDate, first.Status);
        Assert.Equal(first.LastCheckedUtc, second.LastCheckedUtc);
        Assert.Equal(3, handler.RequestCount);
    }

    [Fact]
    public async Task Enabled_check_flags_a_removed_source_that_is_still_used_by_a_saved_rule()
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-02T00:00:00Z"));
        await InitializePlatformAsync(storage, time);
        var source = GuidePackageCatalog.Current.SourceInventory!;
        var removed = source.CustomFormats.First(format => format.MediaType == "tv");
        var handler = new FixedGitHubHandler(source, removed.SourcePath, removeChangedSource: true);
        var service = CreateService(storage, time, handler);
        var quality = new SqliteQualityRepository(storage.Factory, time);
        await quality.CreateCustomFormatAsync(
            new CreateCustomFormatRequest("Saved removed source", "tv", 100, removed.TrashId, "", true),
            CancellationToken.None);

        await service.SetEnabledAsync(true, CancellationToken.None);
        var state = await service.CheckNowAsync(CancellationToken.None);

        var change = Assert.Single(state.Report!.Changes, item => item.SourcePath == removed.SourcePath);
        Assert.Equal("removed", change.ChangeType);
        Assert.True(change.IsInUse);
    }

    private static async Task InitializePlatformAsync(TestStorage storage, TimeProvider time)
    {
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, time),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
    }

    private static GuideUpdateCheckService CreateService(
        TestStorage storage,
        TimeProvider time,
        HttpMessageHandler handler)
        => new(
            new SqliteGuideUpdateCheckStore(storage.Factory, time),
            new SqliteGuidePackageStore(storage.Factory, time),
            new SqliteQualityRepository(storage.Factory, time),
            new GuideUpstreamTreeClient(new FixedClientFactory(handler)),
            time);

    private sealed class FixedClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("https://api.github.com/")
            };
    }

    private sealed class FixedGitHubHandler(
        GuideSourceInventory source,
        string? changedPath,
        bool includeAddedSource = false,
        bool removeChangedSource = false) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            var path = request.RequestUri!.PathAndQuery;
            object response = path switch
            {
                "/repos/TRaSH-Guides/Guides/git/ref/heads/master" => new { @object = new { sha = "remote-guide-revision" } },
                "/repos/TRaSH-Guides/Guides/git/commits/remote-guide-revision" => new { tree = new { sha = "remote-guide-tree" } },
                "/repos/TRaSH-Guides/Guides/git/trees/remote-guide-tree?recursive=1" => new
                {
                    truncated = false,
                    tree = BuildTree()
                },
                _ => throw new InvalidOperationException($"Unexpected request: {path}")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(response), Encoding.UTF8, "application/json")
            });
        }

        private object[] BuildTree()
        {
            var entries = source.CustomFormats
                .Select(format => (format.SourcePath, format.SourceBlobSha))
                .Concat(source.FormatGroups.Select(group => (group.SourcePath, group.SourceBlobSha)))
                .Concat(source.QualityProfiles.Select(profile => (profile.SourcePath, profile.SourceBlobSha)))
                .DistinctBy(item => item.SourcePath, StringComparer.Ordinal)
                .Where(item => !removeChangedSource || !string.Equals(item.SourcePath, changedPath, StringComparison.Ordinal))
                .Select(item => new
                {
                    path = item.SourcePath,
                    type = "blob",
                    sha = string.Equals(item.SourcePath, changedPath, StringComparison.Ordinal)
                        ? "different-remote-blob"
                        : item.SourceBlobSha
                })
                .Cast<object>();
            return includeAddedSource
                ? entries.Append(new { path = "docs/json/radarr/cf/new-upstream-format.json", type = "blob", sha = "new-upstream-blob" }).ToArray()
                : entries.ToArray();
        }
    }
}
