using System.Net;
using System.Text.Json;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Quality.Data;
using Deluno.Quality.Guides;
using Deluno.Quality.ReleasePreferences;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Quality;

/// <summary>
/// The TRaSH Guides update check (coverage register, engines): a check that cannot finish is recorded as Failed with a plain message
/// and is tried again at the next poll instead of waiting a week; a package kept from before source blobs were tracked is told to be
/// updated locally first, and nothing goes to the network for it. Real SQLite, a stub for GitHub that answers 503.
/// </summary>
public sealed class AFailedGuideUpdateCheckIsRecordedPlainlyAndRetriedTests
{
    private sealed class Down : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("https://api.github.com/") };
    }

    private static async Task<GuideUpdateCheckService> StartAsync(TestStorage storage, TimeProvider time, HttpMessageHandler handler)
    {
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, time),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        return new GuideUpdateCheckService(
            new SqliteGuideUpdateCheckStore(storage.Factory, time),
            new SqliteGuidePackageStore(storage.Factory, time),
            new SqliteQualityRepository(storage.Factory, time),
            new GuideUpstreamTreeClient(new Factory(handler)),
            time);
    }

    [Fact]
    public async Task A_check_that_cannot_reach_GitHub_is_recorded_as_failed_in_plain_words_and_tried_again_at_the_next_poll()
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-02T00:00:00Z"));
        var handler = new Down();
        var service = await StartAsync(storage, time, handler);
        await service.SetEnabledAsync(true, CancellationToken.None);

        var first = await service.RunIfDueAsync(CancellationToken.None);
        var afterFirst = handler.Requests;
        var second = await service.RunIfDueAsync(CancellationToken.None);

        Assert.Equal(GuideUpdateCheckStatuses.Failed, first.Status);
        Assert.Contains("503", first.Error);
        Assert.True(afterFirst > 0);
        Assert.Equal(GuideUpdateCheckStatuses.Failed, second.Status);
        Assert.True(handler.Requests > afterFirst, "a failed check is retried at the next poll, not after a week");
    }

    [Fact]
    public async Task A_package_from_before_source_blobs_were_tracked_is_told_to_update_the_local_package_first_and_asks_GitHub_for_nothing()
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-02T00:00:00Z"));
        var handler = new Down();
        var service = await StartAsync(storage, time, handler);
        await service.SetEnabledAsync(true, CancellationToken.None);

        var current = GuidePackageCatalog.Current;
        var inventory = current.SourceInventory!;
        var old = current with
        {
            SourceInventory = inventory with
            {
                SchemaVersion = 1,
                CustomFormats = inventory.CustomFormats.Select(format => format with { SourceBlobSha = string.Empty }).ToArray(),
                FormatGroups = inventory.FormatGroups.Select(group => group with { SourceBlobSha = string.Empty }).ToArray(),
                QualityProfiles = inventory.QualityProfiles.Select(profile => profile with { SourceBlobSha = string.Empty }).ToArray()
            }
        };
        old = JsonSerializer.Deserialize<GuidePackage>(JsonSerializer.Serialize(old, ReleasePreferenceJson.Options), ReleasePreferenceJson.Options)!;
        old = old with { IntegritySha256 = GuidePackageCatalog.ComputeIntegritySha256(old) };
        await using (var connection = await storage.Factory.OpenConnectionAsync(Deluno.Infrastructure.Storage.DelunoDatabaseNames.Platform, CancellationToken.None))
        {
            using var deactivate = connection.CreateCommand();
            deactivate.CommandText = "UPDATE guide_package_versions SET is_active = 0;";
            await deactivate.ExecuteNonQueryAsync();
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO guide_package_versions (package_id, package_version, integrity_sha256, package_json, source_revision, is_active, stored_utc) VALUES (@id, @version, @integrity, @json, @revision, 1, @stored);";
            foreach (var (name, value) in new Dictionary<string, object>
            {
                ["@id"] = old.Id,
                ["@version"] = old.Version,
                ["@integrity"] = old.IntegritySha256!,
                ["@json"] = JsonSerializer.Serialize(old, ReleasePreferenceJson.Options),
                ["@revision"] = inventory.UpstreamRevision,
                ["@stored"] = time.GetUtcNow().ToString("O")
            })
            {
                var parameter = insert.CreateParameter();
                parameter.ParameterName = name;
                parameter.Value = value;
                insert.Parameters.Add(parameter);
            }

            await insert.ExecuteNonQueryAsync();
        }

        var state = await service.CheckNowAsync(CancellationToken.None);

        Assert.Equal(GuideUpdateCheckStatuses.Failed, state.Status);
        Assert.Contains("Update the local guide package", state.Error);
        Assert.Equal(0, handler.Requests);
    }
}
