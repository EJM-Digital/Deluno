using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Realtime;

/// <summary>
/// #1098: every real connection test, whoever ran it, is remembered with its real time, pushed once as
/// <c>ConnectionChecked</c>, and said in Activity only when the result changes.
/// </summary>
public sealed class ConnectionCheckRecorderTests : IDisposable
{
    private readonly TestStorage _storage = TestStorage.Create();
    private readonly FixedTimeProvider _time = new(DateTimeOffset.Parse("2026-10-01T09:00:00Z"));
    private readonly RecordingRealtimeEventPublisher _realtime = new();
    private readonly SqliteJobStore _jobs;
    private readonly ConnectionCheckRecorder _recorder;

    public ConnectionCheckRecorderTests()
    {
        new JobsSchemaInitializer(_storage.Factory, new SqliteDatabaseMigrator(_storage.Factory, _time), NullLogger<JobsSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        _jobs = new SqliteJobStore(_storage.Factory, _time, _realtime, new SqliteDownloadDispatchesRepository(_storage.Factory, _time));
        _recorder = new ConnectionCheckRecorder(_storage.Factory, _jobs, _time, _realtime);
    }

    public void Dispose() => _storage.Dispose();

    private async Task<IReadOnlyList<string>> ConnectionLinesAsync()
        => (await _jobs.ListActivityAsync(50, "connection", null, CancellationToken.None)).Select(item => item.Message).ToArray();

    [Fact]
    public async Task A_check_is_stored_with_its_real_time_and_pushed_with_the_spec_fields()
    {
        var result = await _recorder.RecordAsync(ConnectionKinds.Indexer, "ix1", "NZBgeek", ok: true, latencyMs: 250, "Answered.", CancellationToken.None);

        Assert.True(result.Ok);
        Assert.False(result.Slow);
        var listed = Assert.Single(await _recorder.ListAsync(CancellationToken.None));
        Assert.Equal("ix1", listed.Id);
        Assert.Equal(_time.GetUtcNow(), listed.CheckedUtc);

        var pushed = Assert.Single(_realtime.LiveEventsOf<ConnectionCheckedEvent>(LiveEventNames.ConnectionChecked));
        Assert.Equal(("indexer", "ix1", "NZBgeek", true, false), (pushed.Kind, pushed.Id, pushed.Name, pushed.Ok, pushed.Slow));
        Assert.Equal(_time.GetUtcNow().UtcDateTime.ToString("O"), pushed.CheckedUtc);
    }

    [Fact]
    public async Task A_slow_answer_is_flagged_slow_and_a_failure_never_is()
    {
        var slow = await _recorder.RecordAsync(ConnectionKinds.DownloadClient, "c1", "qBittorrent", true, ConnectionCheckRules.SlowAnswerMs, "Answered.", CancellationToken.None);
        var failed = await _recorder.RecordAsync(ConnectionKinds.DownloadClient, "c2", "SABnzbd", false, 9000, "Timed out.", CancellationToken.None);

        Assert.True(slow.Slow);
        Assert.False(failed.Slow);
        Assert.False(failed.Ok);
    }

    [Fact]
    public async Task Activity_hears_only_when_the_result_changes()
    {
        // A first answer that is fine says nothing.
        await _recorder.RecordAsync(ConnectionKinds.Indexer, "ix1", "NZBgeek", true, 100, "Answered.", CancellationToken.None);
        Assert.Empty(await ConnectionLinesAsync());

        // It stops answering: one plain line.
        _time.Advance(TimeSpan.FromMinutes(15));
        await _recorder.RecordAsync(ConnectionKinds.Indexer, "ix1", "NZBgeek", false, null, "Timed out.", CancellationToken.None);
        // Still down, checked again: nothing new.
        _time.Advance(TimeSpan.FromMinutes(15));
        await _recorder.RecordAsync(ConnectionKinds.Indexer, "ix1", "NZBgeek", false, null, "Timed out.", CancellationToken.None);
        Assert.Equal(["NZBgeek is not answering: Timed out."], await ConnectionLinesAsync());

        // It comes back: one more line.
        _time.Advance(TimeSpan.FromMinutes(15));
        await _recorder.RecordAsync(ConnectionKinds.Indexer, "ix1", "NZBgeek", true, 100, "Answered.", CancellationToken.None);
        var lines = await ConnectionLinesAsync();
        Assert.Equal(2, lines.Count);
        Assert.Contains("NZBgeek is answering again.", lines);

        // Every check pushed, quiet or not.
        Assert.Equal(4, _realtime.LiveEventsOf<ConnectionCheckedEvent>(LiveEventNames.ConnectionChecked).Count);
    }

    [Fact]
    public async Task The_stored_time_moves_on_with_every_check()
    {
        await _recorder.RecordAsync(ConnectionKinds.Lists, "trakt", "Trakt", true, 100, "Answered.", CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(15));
        await _recorder.RecordAsync(ConnectionKinds.Lists, "trakt", "Trakt", true, 100, "Answered.", CancellationToken.None);

        var listed = Assert.Single(await _recorder.ListAsync(CancellationToken.None));
        Assert.Equal(_time.GetUtcNow(), listed.CheckedUtc);
    }
}
