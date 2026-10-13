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
}
