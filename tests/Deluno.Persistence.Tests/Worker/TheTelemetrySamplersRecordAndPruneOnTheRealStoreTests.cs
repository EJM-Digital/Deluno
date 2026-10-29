using System.Net.Http.Json;
using Deluno.Contracts;
using Deluno.Infrastructure.Observability;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Persistence.Tests.RealDisk;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Worker.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using System.Reflection;

namespace Deluno.Persistence.Tests.Worker;

/// <summary>
/// The two samplers behind the Dashboard's speed and machine charts, on the real jobs database: what a pass stores, what it
/// prunes (only readings older than 48 hours, at most once an hour), that the automatic jobs switch does not gate them, and
/// that a reading which fails is a gap in the chart, warned once and then debug-logged.
/// </summary>
public sealed class TheTelemetrySamplersRecordAndPruneOnTheRealStoreTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-20T12:00:00Z");

    private static DownloadTelemetryOverview Overview(double speed = 7.5, int active = 2)
        => new(new DownloadTelemetrySummary(active, 0, 0, 0, 0, 0, speed, 0, 0), [], Now);

    private sealed class ScriptedProbe(Func<string?, MachineTelemetrySample> read) : IMachineProbe
    {
        private readonly List<string?> _volumes = [];
        public IReadOnlyList<string?> Volumes { get { lock (_volumes) { return [.. _volumes]; } } }
        public MachineTelemetrySample Read(string? volumePath) { lock (_volumes) { _volumes.Add(volumePath); } return read(volumePath); }
    }

    /// <summary>An interface whose every member answers with the script.</summary>
    private class Stub : DispatchProxy
    {
        private Func<MethodInfo, object?>? _script;
        public static T Create<T>(Func<MethodInfo, object?> script) where T : class
        {
            var proxy = Create<T, Stub>();
            ((Stub)(object)proxy)._script = script;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            try { return _script!(targetMethod!); }
            catch (Exception exception) when (typeof(Task).IsAssignableFrom(targetMethod!.ReturnType))
            {
                return typeof(Task).GetMethod(nameof(Task.FromException), 1, [typeof(Exception)])!.MakeGenericMethod(targetMethod.ReturnType.GetGenericArguments()).Invoke(null, [exception]);
            }
        }
    }

    private static IServiceScopeFactory ScopeFactory(params (Type Service, object Instance)[] services)
    {
        var collection = new ServiceCollection();
        foreach (var (service, instance) in services) collection.AddSingleton(service, instance);
        return collection.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    [Fact]
    public async Task A_speed_sample_is_stored_and_only_readings_older_than_48_hours_are_pruned_and_not_again_within_the_hour_with_the_switch_off()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        using var switchOff = await app.Client.PutAsJsonAsync("/api/settings/automation", new { isEnabled = false });
        switchOff.EnsureSuccessStatusCode();
        var repository = app.Services.GetRequiredService<IDownloadThroughputRepository>();
        await repository.RecordSampleAsync(new DownloadThroughputSample(Now.AddHours(-49), 1, 1), CancellationToken.None);
        await repository.RecordSampleAsync(new DownloadThroughputSample(Now.AddHours(-47), 2, 1), CancellationToken.None);
        var telemetry = Stub.Create<IDownloadClientTelemetryService>(_ => Task.FromResult(Overview()));
        var time = new FakeTimeProvider(Now);
        var sampler = new DownloadThroughputSampler(
            new TheWorkersOnRealFilesTests.CapturingLogger<DownloadThroughputSampler>(),
            ScopeFactory(
                (typeof(IDownloadClientTelemetryService), telemetry),
                (typeof(IDownloadThroughputRepository), repository),
                (typeof(IPlatformSettingsRepository), app.Services.GetRequiredService<IPlatformSettingsRepository>())),
            time);

        await sampler.SampleAsync(CancellationToken.None);

        var kept = await repository.ListSamplesAsync(Now.AddDays(-5), CancellationToken.None);
        Assert.Equal([Now.AddHours(-47), Now], kept.Select(sample => sample.CapturedUtc));
        Assert.Equal(7.5, kept[1].SpeedMbps);
        Assert.Equal(2, kept[1].ActiveCount);

        // Inside the hour nothing is pruned again, even a reading that has just passed the 48 hours.
        await repository.RecordSampleAsync(new DownloadThroughputSample(Now.AddHours(-48.5), 3, 1), CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(30));
        await sampler.SampleAsync(CancellationToken.None);
        Assert.Contains(await repository.ListSamplesAsync(Now.AddDays(-5), CancellationToken.None), sample => sample.CapturedUtc == Now.AddHours(-48.5));

        // The next pass after the hour prunes it.
        time.Advance(TimeSpan.FromMinutes(31));
        await sampler.SampleAsync(CancellationToken.None);
        Assert.DoesNotContain(await repository.ListSamplesAsync(Now.AddDays(-5), CancellationToken.None), sample => sample.CapturedUtc == Now.AddHours(-48.5));
    }

    [Fact]
    public async Task An_unreachable_client_is_a_gap_the_first_failure_is_a_warning_later_ones_debug_and_stopping_ends_the_loop()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var repository = app.Services.GetRequiredService<IDownloadThroughputRepository>();
        var calls = 0;
        var telemetry = Stub.Create<IDownloadClientTelemetryService>(
            _ => Interlocked.Increment(ref calls) <= 2 ? throw new InvalidOperationException("client unreachable") : Task.FromResult(Overview()));
        var logger = new TheWorkersOnRealFilesTests.CapturingLogger<DownloadThroughputSampler>();
        var time = new FakeTimeProvider(Now);
        var sampler = new DownloadThroughputSampler(
            logger,
            ScopeFactory(
                (typeof(IDownloadClientTelemetryService), telemetry),
                (typeof(IDownloadThroughputRepository), repository),
                (typeof(IPlatformSettingsRepository), app.Services.GetRequiredService<IPlatformSettingsRepository>())),
            time);

        await sampler.StartAsync(CancellationToken.None);
        await Wait.UntilAsync(() => logger.Entries.Count >= 1, "the first failure to be logged");
        time.Advance(TimeSpan.FromMinutes(1));
        await Wait.UntilAsync(() => logger.Entries.Count >= 2, "the second failure to be logged");
        time.Advance(TimeSpan.FromMinutes(1));
        await Wait.UntilAsync(async () => (await repository.ListSamplesAsync(Now.AddHours(-1), CancellationToken.None)).Count == 1, "the third reading to be stored");

        Assert.Equal([LogLevel.Warning, LogLevel.Debug], logger.Entries.Take(2).Select(entry => entry.Level));

        await sampler.StopAsync(CancellationToken.None);
        Assert.True(sampler.ExecuteTask!.IsCompleted);
    }

    [Fact]
    public async Task A_machine_reading_is_stored_against_the_first_library_volume_and_a_library_lookup_that_fails_reads_no_volume()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var root = Path.Combine(app.DataRoot, "movies");
        await app.Services.GetRequiredService<ILibrariesRepository>().CreateLibraryAsync(
            new CreateLibraryRequest("Films", "movies", "library", root, Path.Combine(app.DataRoot, "downloads"), null,
                "direct-import", null, null, null, null, true, true, true, 12, 36, 25),
            CancellationToken.None);
        var repository = app.Services.GetRequiredService<IMachineTelemetryRepository>();
        var probe = new ScriptedProbe(_ => new MachineTelemetrySample(Now, 12.5, 400_000_000, 8_000_000_000, 10, 20, 55.5, 30, 40));
        var sampler = new MachineTelemetrySampler(
            new TheWorkersOnRealFilesTests.CapturingLogger<MachineTelemetrySampler>(),
            ScopeFactory(
                (typeof(IMachineTelemetryRepository), repository),
                (typeof(ILibrariesRepository), app.Services.GetRequiredService<ILibrariesRepository>()),
                (typeof(IPlatformSettingsRepository), app.Services.GetRequiredService<IPlatformSettingsRepository>())),
            probe,
            new FakeTimeProvider(Now));

        await sampler.StartAsync(CancellationToken.None);
        await Wait.UntilAsync(async () => (await repository.ListSamplesAsync(Now.AddHours(-1), CancellationToken.None)).Count == 1, "the first reading to be stored");
        await sampler.StopAsync(CancellationToken.None);

        Assert.Equal(root, probe.Volumes[0]);
        var stored = Assert.Single(await repository.ListSamplesAsync(Now.AddHours(-1), CancellationToken.None));
        Assert.Equal(12.5, stored.CpuPercent);
        Assert.Equal(400_000_000, stored.MemoryBytes);
        Assert.Equal(55.5, stored.DiskBusyPercent);
        Assert.Equal(30, stored.DiskReadBytesPerSecond);
        Assert.Equal(40, stored.DiskWriteBytesPerSecond);
        Assert.Equal(20, stored.ProcessWriteBytesPerSecond);

        var broken = Stub.Create<ILibrariesRepository>(_ => throw new InvalidOperationException("locked"));
        var noVolume = new ScriptedProbe(_ => new MachineTelemetrySample(Now, 1, 1, 2, 0, 0, null, null, null));
        var second = new MachineTelemetrySampler(
            new TheWorkersOnRealFilesTests.CapturingLogger<MachineTelemetrySampler>(),
            ScopeFactory(
                (typeof(IMachineTelemetryRepository), repository),
                (typeof(ILibrariesRepository), broken),
                (typeof(IPlatformSettingsRepository), app.Services.GetRequiredService<IPlatformSettingsRepository>())),
            noVolume,
            new FakeTimeProvider(Now));
        await second.StartAsync(CancellationToken.None);
        await Wait.UntilAsync(() => noVolume.Volumes.Count >= 1, "the second sampler to read");
        await second.StopAsync(CancellationToken.None);
        Assert.Null(noVolume.Volumes[0]);
    }

    [Fact]
    public async Task A_failing_machine_probe_is_a_gap_warned_once_then_debug_and_readings_older_than_48_hours_are_pruned()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var repository = app.Services.GetRequiredService<IMachineTelemetryRepository>();
        await repository.RecordSampleAsync(new MachineTelemetrySample(Now.AddHours(-49), 1, 1, 2, 0, 0, null, null, null), CancellationToken.None);
        await repository.RecordSampleAsync(new MachineTelemetrySample(Now.AddHours(-47), 2, 1, 2, 0, 0, null, null, null), CancellationToken.None);
        var failures = 0;
        var probe = new ScriptedProbe(_ => Interlocked.Increment(ref failures) <= 2
            ? throw new InvalidOperationException("no counters")
            : new MachineTelemetrySample(Now, 9, 1, 2, 0, 0, null, null, null));
        var logger = new TheWorkersOnRealFilesTests.CapturingLogger<MachineTelemetrySampler>();
        var time = new FakeTimeProvider(Now);
        var sampler = new MachineTelemetrySampler(
            logger,
            ScopeFactory(
                (typeof(IMachineTelemetryRepository), repository),
                (typeof(ILibrariesRepository), app.Services.GetRequiredService<ILibrariesRepository>()),
                (typeof(IPlatformSettingsRepository), app.Services.GetRequiredService<IPlatformSettingsRepository>())),
            probe,
            time);

        await sampler.StartAsync(CancellationToken.None);
        await Wait.UntilAsync(() => logger.Entries.Count >= 1, "the first failure to be logged");
        time.Advance(TimeSpan.FromMinutes(1));
        await Wait.UntilAsync(() => logger.Entries.Count >= 2, "the second failure to be logged");
        time.Advance(TimeSpan.FromMinutes(1));
        await Wait.UntilAsync(async () => (await repository.ListSamplesAsync(Now.AddHours(-1), CancellationToken.None)).Count == 1, "the third reading to be stored");
        await sampler.StopAsync(CancellationToken.None);

        Assert.Equal([LogLevel.Warning, LogLevel.Debug], logger.Entries.Take(2).Select(entry => entry.Level));
        var all = await repository.ListSamplesAsync(Now.AddDays(-5), CancellationToken.None);
        Assert.Equal([Now.AddHours(-47), Now], all.Select(sample => sample.CapturedUtc));
    }
}
