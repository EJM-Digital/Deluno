using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Console;

namespace Deluno.Infrastructure.Observability;

/// <summary>
/// The console logging of the headless and Docker host (#1258). It keeps writing to standard output, because that is
/// how <c>docker logs</c> and a service manager's journal read it.
///
/// <para>Why that is safe: the container runtime (the json-file or journald log driver, or none, which discards it) and
/// systemd read standard output as it is written, so the pipe does not fill. And if a reader does stop, as a terminal or
/// a pipe someone left unread can, the console logger's own queue is set to drop the line it cannot write rather than
/// make the caller wait. The host goes on working; the log file (<c>DelunoFileLoggerProvider</c>) has every line.
/// Without this setting a full pipe fills the console logger's queue and then every log call waits, which is how Weir's
/// server hung.</para>
/// </summary>
public static class HostConsoleLogging
{
    public static IServiceCollection MakeNonBlocking(this IServiceCollection services)
        => services.Configure<ConsoleLoggerOptions>(options => options.QueueFullMode = ConsoleLoggerQueueFullMode.DropWrite);
}
