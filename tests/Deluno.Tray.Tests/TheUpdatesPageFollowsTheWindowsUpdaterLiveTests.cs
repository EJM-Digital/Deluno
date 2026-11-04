using System.Reflection;
using Deluno.Api.Updates;
using Deluno.Contracts;
using Deluno.Tray;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Deluno.Tray.Tests;

/// <summary>
/// #1469: on Windows the Updates page waits for its own button because the Velopack orchestrator never pushed
/// <c>UpdateStatusChanged</c>. Each step of a check and a download must push it (a progress step at most about once a
/// second), however the check was started, and reading the status must never push (the page refreshes on a push, so
/// that would loop).
/// </summary>
public sealed class TheUpdatesPageFollowsTheWindowsUpdaterLiveTests : IDisposable
{
    private readonly string? _originalLocalAppData = Environment.GetEnvironmentVariable("DELUNO_TEST_LOCALAPPDATA");
    private readonly string? _originalCommonAppData = Environment.GetEnvironmentVariable("DELUNO_TEST_COMMONAPPDATA");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deluno-update-push-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero));
    private readonly Recorder _recorder;
    private readonly IRealtimeEventPublisher _publisher;

    public TheUpdatesPageFollowsTheWindowsUpdaterLiveTests()
    {
        Velopack.VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        Environment.SetEnvironmentVariable("DELUNO_TEST_LOCALAPPDATA", Path.Combine(_root, "local"));
        Environment.SetEnvironmentVariable("DELUNO_TEST_COMMONAPPDATA", Path.Combine(_root, "common"));
        _publisher = DispatchProxy.Create<IRealtimeEventPublisher, Recorder>();
        _recorder = (Recorder)(object)_publisher;
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("DELUNO_TEST_LOCALAPPDATA", _originalLocalAppData);
        Environment.SetEnvironmentVariable("DELUNO_TEST_COMMONAPPDATA", _originalCommonAppData);
        try { Directory.Delete(_root, recursive: true); } catch { /* temp */ }
    }

    public class Recorder : DispatchProxy
    {
        public List<string> Pushes { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IRealtimeEventPublisher.PublishEntityChangedAsync) && args is { Length: >= 2 })
            {
                lock (Pushes) { Pushes.Add($"{args[0]}:{args[1]}"); }
            }

            return Task.CompletedTask;
        }
    }

    private VelopackUpdateOrchestrator Orchestrator()
        => new(NullLogger<VelopackUpdateOrchestrator>.Instance, null, _publisher, _clock);

    private int UpdatePushes()
    {
        lock (_recorder.Pushes) { return _recorder.Pushes.Count(p => p == "UpdateStatus:updates"); }
    }

    [Fact]
    public async Task A_check_made_through_the_api_pushes()
    {
        var orchestrator = Orchestrator();

        await orchestrator.CheckForUpdatesAsync(CancellationToken.None);

        Assert.True(UpdatePushes() >= 1);
    }

    [Fact]
    public void Each_step_of_a_check_and_a_download_pushes_and_progress_is_throttled_to_about_once_a_second()
    {
        var orchestrator = Orchestrator();
        var runtime = orchestrator.Runtime;

        runtime.MarkChecking();
        Assert.Equal(1, UpdatePushes());
        runtime.MarkUpdateAvailable(_clock.GetUtcNow(), "9.9.9");
        Assert.Equal(2, UpdatePushes());
        runtime.MarkDownloading();
        Assert.Equal(3, UpdatePushes());

        // Ten steps inside half a second add one push; a step a second later adds another.
        _clock.Advance(TimeSpan.FromSeconds(1));
        for (var percent = 1; percent <= 10; percent++)
        {
            orchestrator.ReportDownloadProgress(percent);
            _clock.Advance(TimeSpan.FromMilliseconds(50));
        }

        Assert.Equal(4, UpdatePushes());
        _clock.Advance(TimeSpan.FromSeconds(1));
        orchestrator.ReportDownloadProgress(40);
        Assert.Equal(5, UpdatePushes());

        runtime.MarkDownloaded(_clock.GetUtcNow());
        Assert.Equal(6, UpdatePushes());
        // Apply on a download that is already ready changes nothing the page shows, so it pushes nothing more.
        runtime.MarkReadyToRestart();
        Assert.Equal(6, UpdatePushes());
    }

    [Fact]
    public void A_failed_check_and_an_up_to_date_answer_push()
    {
        var orchestrator = Orchestrator();
        var runtime = orchestrator.Runtime;

        runtime.MarkChecking();
        runtime.MarkError("Couldn't check for updates right now.");
        runtime.MarkChecking();
        runtime.MarkUpToDate(_clock.GetUtcNow(), restartRequired: false);

        Assert.Equal(4, UpdatePushes());
    }

    [Fact]
    public async Task Reading_the_status_never_pushes_so_the_page_cannot_loop()
    {
        var orchestrator = Orchestrator();

        await orchestrator.GetStatusAsync(CancellationToken.None);
        await orchestrator.GetStatusAsync(CancellationToken.None);
        await orchestrator.GetStatusAsync(CancellationToken.None);

        // The first read of a copy that is not installed moves the state to "not supported" once; it never repeats.
        Assert.True(UpdatePushes() <= 1);
    }

    [Fact]
    public async Task The_status_can_be_read_while_a_download_holds_the_updater()
    {
        // Seen on the golden VM (#1469): the pushes arrived, but the page's read of the status waited behind the download
        // (it took the same lock), so the page showed nothing between "Syncing" and 100%.
        var orchestrator = Orchestrator();
        var gate = (SemaphoreSlim)typeof(VelopackUpdateOrchestrator)
            .GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(orchestrator)!;
        await gate.WaitAsync();
        try
        {
            var read = orchestrator.GetStatusAsync(CancellationToken.None);
            var finished = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(5))); // reason: the longest the read may take before it counts as waiting behind the download
            Assert.Same(read, finished);
        }
        finally
        {
            gate.Release();
        }
    }
}
