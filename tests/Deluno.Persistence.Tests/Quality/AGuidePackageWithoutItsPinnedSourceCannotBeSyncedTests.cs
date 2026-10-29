using System.Net;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Quality.Guides;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Deluno.Persistence.Tests.Quality;

public sealed class AGuidePackageWithoutItsPinnedSourceCannotBeSyncedTests
{
    private sealed class HistoricalStore(IGuidePackageStore real, StoredGuidePackage current) : IGuidePackageStore
    {
        public Task<StoredGuidePackage> GetCurrentAsync(CancellationToken cancellationToken) => Task.FromResult(current);
        public Task<IReadOnlyList<StoredGuidePackage>> ListAsync(CancellationToken cancellationToken) => real.ListAsync(cancellationToken);
        public Task<StoredGuidePackage?> GetAsync(string packageId, int version, CancellationToken cancellationToken) => real.GetAsync(packageId, version, cancellationToken);
        public Task<GuidePackageUpdatePreview> PreviewAsync(GuidePackageUpdateRequest request, CancellationToken cancellationToken) => real.PreviewAsync(request, cancellationToken);
        public Task<StoredGuidePackage> ApplyAsync(GuidePackageUpdateRequest request, CancellationToken cancellationToken) => real.ApplyAsync(request, cancellationToken);
        public Task<StoredGuidePackage> ActivateAsync(string packageId, int version, CancellationToken cancellationToken) => real.ActivateAsync(packageId, version, cancellationToken);
    }

    private sealed class NeverAsked : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    [Fact]
    public async Task A_historical_package_with_no_pinned_source_inventory_makes_the_sync_preview_unavailable_in_plain_words_without_asking_upstream()
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-02T00:00:00Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, time),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var real = new SqliteGuidePackageStore(storage.Factory, time);
        var shipped = await real.GetCurrentAsync(CancellationToken.None);
        var historical = shipped with { Package = shipped.Package with { SourceInventory = null, IntegritySha256 = null } };
        var upstream = new NeverAsked();
        var service = new GuidePackageSyncService(
            new HistoricalStore(real, historical),
            new GuideUpstreamTreeClient(new Factory(upstream)));

        var preview = await service.PreviewAsync(new GuidePackageSyncRequest(historical.IntegritySha256, "any-revision"), CancellationToken.None);

        Assert.False(preview.CanApply);
        Assert.Contains(preview.Errors, error => error.Contains("has no pinned source inventory", StringComparison.Ordinal));
        Assert.Equal(0, upstream.Requests);
    }
}
