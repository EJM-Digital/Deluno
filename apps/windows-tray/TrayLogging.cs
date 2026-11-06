using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Console;

namespace Deluno.Tray;

/// <summary>
/// The web host builder the tray and the Windows service both use, and the logging it comes with (#1258).
///
/// <para><c>WebApplication.CreateBuilder</c> adds the console logger. Deluno.exe is a windowed program that inherits
/// whatever standard output its launcher gave it (Setup, Velopack's update hooks, a scheduled task, a relaunch). If that
/// is a pipe nobody reads, the console logger's writer stops once the pipe is full, its queue fills, and every log call
/// then waits: Deluno freezes with no line in any log to say why. That is how Weir's server hung. So the console logger is
/// not registered here at all; the file logger (<c>DelunoFileLoggerProvider</c>, added by <c>AddDelunoInfrastructure</c>)
/// and tray-startup.log are the record. <see cref="StandardHandles"/> closes the other door, for code that writes to the
/// console without the logger.</para>
/// </summary>
internal static class TrayLogging
{
    internal static WebApplicationBuilder CreateBuilder(WebApplicationOptions options)
    {
        var builder = WebApplication.CreateBuilder(options);
        foreach (var console in builder.Services
                     .Where(descriptor => descriptor.ServiceType == typeof(ILoggerProvider)
                                          && descriptor.ImplementationType == typeof(ConsoleLoggerProvider))
                     .ToList())
        {
            builder.Services.Remove(console);
        }

        return builder;
    }

    /// <summary>
    /// The Windows service lifetime adds the Windows event log as a log target, and Event Viewer is where a person looks first,
    /// so it stays. But host shutdown disposes that source while a background service's last failure is still being written
    /// to it, and the write threw on a task nobody reads (#1524: "Cannot access a disposed object, EventLogInternal"). The
    /// provider is wrapped so a write after disposal is dropped and recorded once in the file log.
    /// </summary>
    internal static void MakeEventLogSafe(IServiceCollection services)
    {
        foreach (var eventLog in services
                     .Where(descriptor => descriptor.ServiceType == typeof(ILoggerProvider)
                                          && descriptor.ImplementationType == typeof(Microsoft.Extensions.Logging.EventLog.EventLogLoggerProvider))
                     .ToList())
        {
            var index = services.IndexOf(eventLog);
            services.RemoveAt(index);
            services.Insert(index, ServiceDescriptor.Singleton<ILoggerProvider>(provider =>
                new DisposalSafeLoggerProvider(
                    (ILoggerProvider)ActivatorUtilities.CreateInstance(provider, typeof(Microsoft.Extensions.Logging.EventLog.EventLogLoggerProvider)),
                    message => HookLog.Write(message, TrayStartLog.FileName))));
        }
    }
}

/// <summary>Passes writes to a log target until it is disposed; after that (or when it throws) drops them and says so once.</summary>
internal sealed class DisposalSafeLoggerProvider(ILoggerProvider inner, Action<string> recordOnce) : ILoggerProvider, ISupportExternalScope
{
    private int _disposed;
    private int _recorded;

    internal bool IsDisposed => Volatile.Read(ref _disposed) == 1;

    public ILogger CreateLogger(string categoryName) => new SafeLogger(this, inner.CreateLogger(categoryName));

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => (inner as ISupportExternalScope)?.SetScopeProvider(scopeProvider);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        try { inner.Dispose(); }
        catch (Exception exception) { RecordOnce($"The Windows event log could not be closed cleanly: {exception.Message}"); }
    }

    private void RecordOnce(string message)
    {
        if (Interlocked.Exchange(ref _recorded, 1) == 0)
        {
            try { recordOnce(message); }
            catch { /* the record is best-effort */ }
        }
    }

    private sealed class SafeLogger(DisposalSafeLoggerProvider owner, ILogger inner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner.IsDisposed ? null : inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => !owner.IsDisposed && inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (owner.IsDisposed)
            {
                owner.RecordOnce("A log line was written to the Windows event log after it was closed at shutdown, so it was left out of the event log (the file logs have it).");
                return;
            }

            try
            {
                inner.Log(logLevel, eventId, state, exception, formatter);
            }
            catch (Exception failure)
            {
                owner.RecordOnce($"The Windows event log refused a log line ({failure.GetType().Name}: {failure.Message}); further lines for it may be left out of the event log.");
            }
        }
    }
}
