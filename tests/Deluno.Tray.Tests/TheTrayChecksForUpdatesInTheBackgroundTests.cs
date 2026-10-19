using System.Reflection;
using System.Text.Json;
using Deluno.Api.Updates;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Tray.Tests;

/// <summary>
/// The tray's background update check: about 20 seconds after the tray starts it asks for a new release when "auto check
/// updates" is on, says nothing and asks nothing when it is off, and a check that fails is swallowed so the tray goes on.
/// The wait is the real 20 seconds, because the service has no clock to replace; the tray's tests run one after another, so
/// the settings folder the service reads is the test's own.
/// </summary>
public sealed class TheTrayChecksForUpdatesInTheBackgroundTests : IDisposable
{
    private readonly string? _originalLocalAppData = Environment.GetEnvironmentVariable("DELUNO_TEST_LOCALAPPDATA");
    private readonly string? _originalCommonAppData = Environment.GetEnvironmentVariable("DELUNO_TEST_COMMONAPPDATA");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deluno-tray-tests", Guid.NewGuid().ToString("N"));

    public TheTrayChecksForUpdatesInTheBackgroundTests()
    {
        Environment.SetEnvironmentVariable("DELUNO_TEST_LOCALAPPDATA", Path.Combine(_root, "local"));
        Environment.SetEnvironmentVariable("DELUNO_TEST_COMMONAPPDATA", Path.Combine(_root, "common"));
    }

    [Fact]
    public async Task With_auto_check_on_a_check_is_made_after_the_startup_wait_and_a_failed_check_does_not_stop_the_tray()
    {
        // Saying Yes in the setup guide is what writes this; a fresh install has no file and does not check (#1361).
        Assert.Null(AppSettings.Update(saved => { saved.AutoCheckUpdates = true; saved.DataRoot = Path.Combine(_root, "data"); }));
        var orchestrator = Orchestrator.Create(throwOnCheck: true);
        var service = new TrayUpdateBackgroundService(orchestrator.Proxy, NullLogger<TrayUpdateBackgroundService>.Instance);

        await service.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal(0, orchestrator.Checks);
            await Wait.UntilAsync(() => orchestrator.Checks >= 1, "the first update check", TimeSpan.FromSeconds(40));
            await Task.Delay(300); // reason: gives a failed check time to leak out of the service if it were going to
            Assert.False(service.ExecuteTask!.IsCompleted, "a failed check must not end the service");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        Assert.Equal(1, orchestrator.Checks);
    }

    [Fact]
    public async Task A_fresh_install_with_no_settings_file_makes_no_check()
    {
        var orchestrator = Orchestrator.Create(throwOnCheck: false);
        var service = new TrayUpdateBackgroundService(orchestrator.Proxy, NullLogger<TrayUpdateBackgroundService>.Instance);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(22)); // reason: the service's first look is at 20 s; showing no check means waiting past it
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        Assert.Equal(0, orchestrator.Checks);
    }

    [Fact]
    public async Task With_auto_check_off_no_check_is_made()
    {
        var configPath = Path.Combine(_root, "local", "DelunoConfig", "deluno.json");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, JsonSerializer.Serialize(new AppSettings { AutoCheckUpdates = false, DataRoot = Path.Combine(_root, "data") }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        var orchestrator = Orchestrator.Create(throwOnCheck: false);
        var service = new TrayUpdateBackgroundService(orchestrator.Proxy, NullLogger<TrayUpdateBackgroundService>.Instance);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(22)); // reason: the service's first look is at 20 s; showing no check means waiting past it
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        Assert.Equal(0, orchestrator.Checks);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("DELUNO_TEST_LOCALAPPDATA", _originalLocalAppData);
        Environment.SetEnvironmentVariable("DELUNO_TEST_COMMONAPPDATA", _originalCommonAppData);
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private class Orchestrator : DispatchProxy
    {
        private int _checks;
        private bool _throwOnCheck;

        public int Checks => Volatile.Read(ref _checks);

        public IUpdateOrchestrator Proxy => (IUpdateOrchestrator)(object)this;

        public static Orchestrator Create(bool throwOnCheck)
        {
            var proxy = DispatchProxy.Create<IUpdateOrchestrator, Orchestrator>();
            var orchestrator = (Orchestrator)(object)proxy;
            orchestrator._throwOnCheck = throwOnCheck;
            return orchestrator;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(IUpdateOrchestrator.CheckForUpdatesAsync))
            {
                throw new NotSupportedException(targetMethod?.Name);
            }

            Interlocked.Increment(ref _checks);
            return _throwOnCheck ? throw new HttpRequestException("the release feed could not be reached") : Task.FromResult<UpdateStatusResponse>(null!);
        }
    }
}
