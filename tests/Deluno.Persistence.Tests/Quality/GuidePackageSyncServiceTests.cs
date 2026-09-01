using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Quality.Guides;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Deluno.Persistence.Tests.Quality;

public sealed class GuidePackageSyncServiceTests
{
    [Fact]
    public async Task Preview_then_apply_pins_the_exact_reviewed_upstream_snapshot_without_rewriting_reviewed_mapping()
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-02T00:00:00Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, time),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var store = new SqliteGuidePackageStore(storage.Factory, time);
        var current = await store.GetCurrentAsync(CancellationToken.None);
        var reviewed = current.Package.CustomFormats.First(format => format.MappingStatus == GuideMappingStatus.Reviewed);
        var upstream = current.Package.SourceInventory!.CustomFormats.Single(format => format.TrashId == reviewed.TrashId);
        var fixture = SyncFixture.Create(upstream);
        var service = new GuidePackageSyncService(
            store,
            new GuideUpstreamTreeClient(new FixedClientFactory(new SyncHandler(fixture))));
        var request = new GuidePackageSyncRequest(current.IntegritySha256, fixture.Revision);

        var preview = await service.PreviewAsync(request, CancellationToken.None);

        Assert.True(preview.CanApply, string.Join(" | ", preview.Errors));
        Assert.Equal(current.Package.Version + 1, preview.Proposed.Version);
        Assert.Equal(fixture.Revision, preview.Proposed.Source.UpstreamRevision);
        Assert.Equal(fixture.Revision, preview.Proposed.SourceInventory!.UpstreamRevision);
        var syncedFormat = preview.Proposed.CustomFormats.Single(format => format.TrashId == reviewed.TrashId);
        Assert.Equal(reviewed.MappingStatus, syncedFormat.MappingStatus);
        Assert.Equal(reviewed.MappedTraitIds, syncedFormat.MappedTraitIds);
        Assert.Equal("remote-custom-format-blob", preview.Proposed.SourceInventory.CustomFormats.Single().SourceBlobSha);

        var applied = await service.ApplyAsync(
            request with { ExpectedProposedIntegritySha256 = preview.ProposedIntegritySha256 },
            CancellationToken.None);

        Assert.True(applied.IsActive);
        Assert.Equal(preview.ProposedIntegritySha256, applied.IntegritySha256);
        Assert.Equal(fixture.Revision, (await store.GetCurrentAsync(CancellationToken.None)).Package.Source.UpstreamRevision);
    }

    [Fact]
    public async Task Apply_rejects_a_candidate_that_was_not_the_exact_preview()
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-02T00:00:00Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, time),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var store = new SqliteGuidePackageStore(storage.Factory, time);
        var current = await store.GetCurrentAsync(CancellationToken.None);
        var upstream = current.Package.SourceInventory!.CustomFormats.First();
        var fixture = SyncFixture.Create(upstream);
        var service = new GuidePackageSyncService(
            store,
            new GuideUpstreamTreeClient(new FixedClientFactory(new SyncHandler(fixture))));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync(
            new GuidePackageSyncRequest(current.IntegritySha256, fixture.Revision, "not-the-preview-hash"),
            CancellationToken.None));
        Assert.Equal(current.IntegritySha256, (await store.GetCurrentAsync(CancellationToken.None)).IntegritySha256);
    }

    private sealed record SyncFixture(
        string Revision,
        IReadOnlyDictionary<string, string> SourceTextByPath,
        IReadOnlyDictionary<string, string> BlobShaByPath)
    {
        public static SyncFixture Create(GuideSourceCustomFormat source)
        {
            const string revision = "remote-guide-revision";
            var groupPath = "docs/json/radarr/cf-groups/fixture-group.json";
            var profilePath = "docs/json/radarr/quality-profiles/fixture-profile.json";
            var customFormat = JsonSerializer.Serialize(new
            {
                trash_id = source.TrashId,
                name = "Remote fixture custom format",
                trash_description = "Fixture source used only to prove the sync boundary.",
                trash_scores = new Dictionary<string, int> { ["default"] = 0 },
                includeCustomFormatWhenRenaming = false,
                specifications = Array.Empty<object>()
            });
            var group = JsonSerializer.Serialize(new
            {
                trash_id = "fixture-format-group",
                name = "Fixture group",
                custom_formats = new[] { new { trash_id = source.TrashId, name = "Fixture custom format", required = false } },
                quality_profiles = new { include = new Dictionary<string, string> { ["Fixture profile"] = "fixture-quality-profile" } }
            });
            var profile = JsonSerializer.Serialize(new
            {
                trash_id = "fixture-quality-profile",
                name = "Fixture quality profile",
                formatItems = new Dictionary<string, string> { ["Fixture custom format"] = source.TrashId }
            });
            return new SyncFixture(
                revision,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [source.SourcePath] = customFormat,
                    [groupPath] = group,
                    [profilePath] = profile
                },
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [source.SourcePath] = "remote-custom-format-blob",
                    [groupPath] = "remote-group-blob",
                    [profilePath] = "remote-profile-blob"
                });
        }
    }

    private sealed class FixedClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(handler, disposeHandler: false) { BaseAddress = new Uri("https://api.github.com/") };
    }

    private sealed class SyncHandler(SyncFixture fixture) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (string.Equals(uri.Host, "codeload.github.com", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(CreateArchive(fixture.SourceTextByPath))
                });
            }

            object response = uri.PathAndQuery switch
            {
                "/repos/TRaSH-Guides/Guides/git/ref/heads/master" => new { @object = new { sha = fixture.Revision } },
                "/repos/TRaSH-Guides/Guides/git/commits/remote-guide-revision" => new { tree = new { sha = "remote-guide-tree" } },
                "/repos/TRaSH-Guides/Guides/git/trees/remote-guide-tree?recursive=1" => new
                {
                    truncated = false,
                    tree = fixture.BlobShaByPath.Select(pair => new { path = pair.Key, type = "blob", sha = pair.Value }).ToArray()
                },
                _ => throw new InvalidOperationException($"Unexpected request: {uri}")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(response), Encoding.UTF8, "application/json")
            });
        }

        private static byte[] CreateArchive(IReadOnlyDictionary<string, string> sourceTextByPath)
        {
            using var stream = new MemoryStream();
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (path, sourceText) in sourceTextByPath)
                {
                    var entry = archive.CreateEntry($"Guides-remote/{path}");
                    using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                    writer.Write(sourceText);
                }
            }
            return stream.ToArray();
        }
    }
}
