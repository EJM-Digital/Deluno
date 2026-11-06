using System.Runtime.CompilerServices;
using Deluno.Contracts;
using Deluno.Tray;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>
/// #1524: install-hook.log held unobserved task exceptions from the service paths: a connect left running past its limit
/// (socket error 995 from TcpClient.CompleteConnectAsync) and an event-log write through a disposed source.
/// </summary>
[Collection("UnobservedTaskExceptions")]
public sealed class AnAbandonedTaskLeavesNoUnobservedExceptionTests
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AbandonAFailingConnect(bool observe)
    {
        var gate = new TaskCompletionSource();
        // The same shape as the log: a connect that fails with a socket error after the caller stopped waiting.
        var connect = gate.Task.ContinueWith(
            _ => throw new System.Net.Sockets.SocketException(995),
            TaskScheduler.Default);
        if (observe)
        {
            connect.Observed();
        }

        // Nobody waits for it (the 1 s limit passed); the connect then fails.
        gate.SetResult();
        Thread.Sleep(50); // reason: lets the failing continuation finish before the task is dropped; 50 ms is far longer than it needs
    }

    private static int CountUnobservedAfter(bool observe)
    {
        var seen = 0;
        void Handler(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            Interlocked.Increment(ref seen);
            e.SetObserved();
        }

        TaskScheduler.UnobservedTaskException += Handler;
        try
        {
            AbandonAFailingConnect(observe);
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Handler;
        }

        return seen;
    }

    [Fact]
    public void Without_observing_the_abandoned_failure_reaches_the_finalizer()
        => Assert.True(CountUnobservedAfter(observe: false) > 0, "the control must show the problem, or the test below proves nothing");

    [Fact]
    public void An_abandoned_task_that_is_observed_leaves_nothing_behind()
        => Assert.Equal(0, CountUnobservedAfter(observe: true));

    private sealed class DisposableTarget : ILoggerProvider, ILogger
    {
        public bool Disposed;
        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() => Disposed = true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (Disposed)
            {
                throw new ObjectDisposedException("EventLogInternal");
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (int Records, int Unobserved) WriteAfterDisposal()
    {
        var records = 0;
        var unobserved = 0;
        void Handler(object? s, UnobservedTaskExceptionEventArgs e) { Interlocked.Increment(ref unobserved); e.SetObserved(); }
        TaskScheduler.UnobservedTaskException += Handler;
        try
        {
            var target = new DisposableTarget();
            var provider = new DisposalSafeLoggerProvider(target, _ => Interlocked.Increment(ref records));
            var logger = provider.CreateLogger("Test");
            logger.LogError("before");
            provider.Dispose();
            logger.LogError(new InvalidOperationException("late"), "after");
            logger.LogError("again");
            for (var i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Handler;
        }

        return (records, unobserved);
    }

    [Fact]
    public void A_write_after_the_event_log_is_disposed_does_not_throw_and_is_recorded_once()
    {
        var (records, unobserved) = WriteAfterDisposal();
        Assert.Equal(1, records);
        Assert.Equal(0, unobserved);
    }

    [Fact]
    public void The_service_keeps_the_windows_event_log_but_wraps_it()
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddEventLog());

        TrayLogging.MakeEventLogSafe(services);

        Assert.DoesNotContain(services, d => d.ImplementationType == typeof(EventLogLoggerProvider));
        Assert.Contains(services, d => d.ServiceType == typeof(ILoggerProvider) && d.ImplementationFactory is not null);
        using var built = services.BuildServiceProvider();
        Assert.Contains(built.GetServices<ILoggerProvider>(), p => p is DisposalSafeLoggerProvider);
    }

    [Fact]
    public void The_service_host_wraps_the_event_log_after_the_windows_service_lifetime_adds_it()
    {
        var source = File.ReadAllText(Path.Combine(Root(), "apps", "windows-tray", "ServiceHost.cs"));
        var useService = source.IndexOf("UseWindowsService(", StringComparison.Ordinal);
        var remove = source.IndexOf("TrayLogging.MakeEventLogSafe", StringComparison.Ordinal);
        Assert.True(useService >= 0 && remove > useService);
    }

    [Fact]
    public void No_connect_is_abandoned_without_being_observed()
    {
        var sabnzbd = File.ReadAllText(Path.Combine(Root(), "apps", "windows-tray", "SabnzbdService.cs"));
        Assert.DoesNotContain("ConnectAsync(\"127.0.0.1\", 8080).Wait(", sabnzbd);
        Assert.Contains(".Observed()", sabnzbd);
        var monitor = File.ReadAllText(Path.Combine(Root(), "src", "Deluno.Platform", "Connectivity", "ConnectivityMonitor.cs"));
        Assert.Contains(".Observed()", monitor);
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Deluno.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }
}
