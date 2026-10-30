using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Infrastructure.Caching;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Jobs;
using Deluno.Quality.ReleasePreferences;
using Deluno.Recovery.Services;
using Deluno.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Deluno.Platform.Tests.Contracts;

/// <summary>
/// A handful of small services and rules: the retry marker that keeps a job quiet in Activity, the
/// Proper/Repack mode names, the secret protector, the short-lived memo, the engine-picking unmonitor
/// composite, the start-up indexer rename and the defaults a migration inherits.
/// </summary>
public sealed class SmallServicesTests
{
    [Theory]
    [InlineData("""{"retriedBecause":"the output moved"}""", true)]
    [InlineData("""{"x":1,"RetriedBecause":"again"}""", true)]
    [InlineData("""{"retriedBecause":"  "}""", false)]
    [InlineData("""{"retriedBecause":5}""", false)]
    [InlineData("""{"other":"retriedBecause"}""", false)]
    [InlineData("""["retriedBecause"]""", false)]
    [InlineData("retriedBecause but not json", false)]
    [InlineData("""{"nothing":"here"}""", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void A_job_is_quiet_only_when_its_request_says_why_it_is_being_retried(string? payload, bool quiet)
        => Assert.Equal(quiet, JobActivityQuiet.IsQuiet(payload));

    [Theory]
    [InlineData("do-not-upgrade-automatically", ProperRepackAcquisitionModes.DoNotUpgradeAutomatically)]
    [InlineData(" DO-NOT-PREFER ", ProperRepackAcquisitionModes.DoNotPrefer)]
    [InlineData("prefer-and-upgrade", ProperRepackAcquisitionModes.PreferAndUpgrade)]
    [InlineData("anything else", ProperRepackAcquisitionModes.PreferAndUpgrade)]
    [InlineData(null, ProperRepackAcquisitionModes.PreferAndUpgrade)]
    public void A_proper_repack_mode_is_normalised_to_a_known_one(string? value, string expected)
        => Assert.Equal(expected, ProperRepackAcquisitionModes.Normalize(value));

    [Fact]
    public void A_secret_is_protected_with_a_marker_and_read_back_and_a_plain_value_passes_through()
    {
        ISecretProtector protector = new DataProtectionSecretProtector(new EphemeralDataProtectionProvider());
        var stored = protector.Protect("indexer", "api-key-123");

        Assert.StartsWith("dp:v1:", stored);
        Assert.DoesNotContain("api-key-123", stored);
        Assert.True(protector.IsProtected(stored));
        Assert.Equal("api-key-123", protector.Unprotect("indexer", stored));
        Assert.Throws<System.Security.Cryptography.CryptographicException>(() => protector.Unprotect("another-purpose", stored));

        Assert.Equal("legacy-plain", protector.Unprotect("indexer", "legacy-plain"));
        Assert.False(protector.IsProtected("legacy-plain"));
        Assert.False(protector.IsProtected(null));
        Assert.False(protector.IsProtected("  "));
        Assert.Null(protector.Unprotect("indexer", null));
        Assert.Null(protector.Unprotect("indexer", " "));
        Assert.Throws<ArgumentException>(() => protector.Protect("indexer", ""));
        Assert.Throws<ArgumentException>(() => protector.Protect("indexer", "  "));
    }

    [Fact]
    public async Task The_memo_keeps_an_answer_until_it_expires_or_the_data_underneath_changes()
    {
        var clock = new ManualClock();
        var memo = new GenerationMemo<int>(clock);
        var computed = 0;
        Task<int> Compute() => Task.FromResult(++computed);

        Assert.Equal(1, await memo.GetOrAddAsync("k", 1, TimeSpan.FromSeconds(5), Compute));
        Assert.Equal(1, await memo.GetOrAddAsync("k", 1, TimeSpan.FromSeconds(5), Compute));

        Assert.Equal(2, await memo.GetOrAddAsync("k", 2, TimeSpan.FromSeconds(5), Compute));
        clock.Now += TimeSpan.FromSeconds(6);
        Assert.Equal(3, await memo.GetOrAddAsync("k", 2, TimeSpan.FromSeconds(5), Compute));
        Assert.Equal(10, await memo.GetOrAddAsync("other", 2, TimeSpan.FromSeconds(5), () => Task.FromResult(10)));

        memo.Clear();
        Assert.Equal(4, await memo.GetOrAddAsync("k", 2, TimeSpan.FromSeconds(5), Compute));
    }

    [Fact]
    public async Task A_computation_that_throws_is_not_kept_and_the_next_caller_tries_again()
    {
        var memo = new GenerationMemo<int>();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            memo.GetOrAddAsync("k", 1, TimeSpan.FromMinutes(1), () => Task.FromException<int>(new InvalidOperationException("db busy"))));
        Assert.Equal(7, await memo.GetOrAddAsync("k", 1, TimeSpan.FromMinutes(1), () => Task.FromResult(7)));
    }

    [Fact]
    public async Task A_caller_that_gives_up_does_not_cancel_the_shared_computation()
    {
        var memo = new GenerationMemo<int>();
        var release = new TaskCompletionSource<int>();
        using var cts = new CancellationTokenSource();
        var first = memo.GetOrAddAsync("k", 1, TimeSpan.FromMinutes(1), () => release.Task, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        release.SetResult(42);
        Assert.Equal(42, await memo.GetOrAddAsync("k", 1, TimeSpan.FromMinutes(1), () => Task.FromResult(-1)));
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-10-31T00:00:00Z");

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static Mock<IDispatchTitleUnmonitorComponent> Component(string handles, bool? hasFile, bool unmonitored)
    {
        var component = new Mock<IDispatchTitleUnmonitorComponent>();
        component.Setup(c => c.CanHandle(It.IsAny<string>())).Returns<string>(type => type == handles);
        component.Setup(c => c.HasFileAsync(handles, It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(hasFile);
        component.Setup(c => c.UnmonitorAsync(handles, It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(unmonitored);
        return component;
    }

    [Fact]
    public async Task The_unmonitor_composite_hands_each_call_to_the_engine_that_owns_the_entity_type()
    {
        var movies = Component("movie", true, true);
        var series = Component("series", false, false);
        var composite = new CompositeDispatchTitleUnmonitorService([movies.Object, series.Object]);

        Assert.True(composite.CanHandle("movie"));
        Assert.True(composite.CanHandle("series"));
        Assert.False(composite.CanHandle("album"));
        Assert.True(await composite.HasFileAsync("movie", "m-1", CancellationToken.None));
        Assert.False(await composite.HasFileAsync("series", "s-1", CancellationToken.None));
        Assert.Null(await composite.HasFileAsync("album", "a-1", CancellationToken.None));
        Assert.True(await composite.UnmonitorAsync("movie", "m-1", CancellationToken.None));
        Assert.False(await composite.UnmonitorAsync("series", "s-1", CancellationToken.None));
        Assert.False(await composite.UnmonitorAsync("album", "a-1", CancellationToken.None));
        series.Verify(c => c.HasFileAsync("movie", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task At_start_up_every_indexer_takes_its_automatic_name_and_a_failure_never_stops_the_start()
    {
        var connections = new Mock<IConnectionsRepository>();
        connections.SetupSequence(c => c.ApplyAutomaticIndexerNamesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(3)
            .ReturnsAsync(0)
            .ThrowsAsync(new InvalidOperationException("db locked"));
        var service = new IndexerNamesUpgradeService(connections.Object, NullLogger<IndexerNamesUpgradeService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await service.StartAsync(CancellationToken.None);
        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
        connections.Verify(c => c.ApplyAutomaticIndexerNamesAsync(It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    public async Task A_cancelled_start_up_rename_is_not_swallowed()
    {
        var connections = new Mock<IConnectionsRepository>();
        connections.Setup(c => c.ApplyAutomaticIndexerNamesAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException());
        var service = new IndexerNamesUpgradeService(connections.Object, NullLogger<IndexerNamesUpgradeService>.Instance);
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.StartAsync(CancellationToken.None));
    }

    [Fact]
    public void A_migration_that_overrides_nothing_has_no_superseded_checksums_and_does_not_compact()
    {
        var migration = new Mock<IDelunoDatabaseMigration> { CallBase = true }.Object;
        Assert.Empty(migration.SupersededChecksums);
        Assert.False(migration.CompactsDatabaseAfterApply);
    }
}
