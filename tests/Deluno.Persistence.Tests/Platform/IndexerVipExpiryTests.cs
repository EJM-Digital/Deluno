using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Platform;

/// <summary>
/// VIP/subscription expiry tracking per private-tracker indexer (#747): a
/// warning starts 7 days before the recorded date, continues after it
/// lapses, and clears once acknowledged or the date itself changes.
/// </summary>
public sealed class IndexerVipExpiryTests
{
    private static async Task<SqliteConnectionsRepository> CreateRepositoryAsync(TestStorage storage, DateTimeOffset now)
    {
        var timeProvider = new FixedTimeProvider(now);
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        return new SqliteConnectionsRepository(storage.Factory, timeProvider, TestSecretProtection.Create(storage));
    }

    private static CreateIndexerRequest BaseCreateRequest() =>
        new(Name: "Private Tracker",
            Protocol: "torznab",
            Privacy: "private",
            BaseUrl: "https://tracker.example.test",
            ApiKey: "secret-key",
            Priority: 5,
            Categories: "2000, 5000",
            Tags: "",
            MediaScope: "movies",
            IsEnabled: true);

    [Theory]
    [InlineData(30, false)]  // more than 7 days out: no warning yet
    [InlineData(7, true)]    // exactly at the 7-day threshold
    [InlineData(3, true)]    // inside the window
    [InlineData(-1, true)]   // already lapsed
    public void IsActive_warns_from_7_days_before_expiry_and_after_it_lapses(int daysUntilExpiry, bool expected)
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var expiry = now.AddDays(daysUntilExpiry);

        Assert.Equal(expected, IndexerVipWarning.IsActive(expiry, acknowledgedForExpiryUtc: null, now));
    }

    [Fact]
    public void IsActive_is_false_when_nothing_is_recorded()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        Assert.False(IndexerVipWarning.IsActive(null, null, now));
    }

    [Fact]
    public async Task Acknowledging_silences_the_warning_for_the_recorded_expiry()
    {
        using var storage = TestStorage.Create();
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var repo = await CreateRepositoryAsync(storage, now);
        var expiry = now.AddDays(3);
        var created = await repo.CreateIndexerAsync(BaseCreateRequest() with { VipExpiresUtc = expiry }, CancellationToken.None);

        Assert.True(IndexerVipWarning.IsActive(created, now));

        var acknowledged = await repo.UpdateIndexerAsync(
            created.Id,
            new UpdateIndexerRequest(null, null, null, null, null, null, null, null, null, null, AcknowledgeVipExpiry: true),
            CancellationToken.None);

        Assert.NotNull(acknowledged);
        Assert.False(IndexerVipWarning.IsActive(acknowledged, now));
    }

    [Fact]
    public async Task Warning_returns_when_the_recorded_date_changes_after_an_acknowledgement()
    {
        using var storage = TestStorage.Create();
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var repo = await CreateRepositoryAsync(storage, now);
        var created = await repo.CreateIndexerAsync(BaseCreateRequest() with { VipExpiresUtc = now.AddDays(3) }, CancellationToken.None);
        await repo.UpdateIndexerAsync(
            created.Id,
            new UpdateIndexerRequest(null, null, null, null, null, null, null, null, null, null, AcknowledgeVipExpiry: true),
            CancellationToken.None);

        var renewedButStillSoon = await repo.UpdateIndexerAsync(
            created.Id,
            new UpdateIndexerRequest(null, null, null, null, null, null, null, null, null, null, VipExpiresUtc: now.AddDays(2)),
            CancellationToken.None);

        Assert.NotNull(renewedButStillSoon);
        Assert.True(IndexerVipWarning.IsActive(renewedButStillSoon, now));
    }

    [Fact]
    public async Task Clearing_the_expiry_drops_the_warning_and_the_acknowledgement()
    {
        using var storage = TestStorage.Create();
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var repo = await CreateRepositoryAsync(storage, now);
        var created = await repo.CreateIndexerAsync(BaseCreateRequest() with { VipExpiresUtc = now.AddDays(3) }, CancellationToken.None);

        var cleared = await repo.UpdateIndexerAsync(
            created.Id,
            new UpdateIndexerRequest(null, null, null, null, null, null, null, null, null, null, ClearVipExpiry: true),
            CancellationToken.None);

        Assert.NotNull(cleared);
        Assert.Null(cleared.VipExpiresUtc);
        Assert.Null(cleared.VipWarningAcknowledgedExpiryUtc);
        Assert.False(IndexerVipWarning.IsActive(cleared, now));
    }
}
