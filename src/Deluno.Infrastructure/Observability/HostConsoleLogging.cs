using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Console;

namespace Deluno.Infrastructure.Observability;

/// <summary>
/// The console logging of the headless and Docker host (#1258). It keeps writing to standard output, because that is
/// how <c>docker logs</c> and a service manager's journal read it.
/// </summary>
public static class HostConsoleLogging
{
    public static IServiceCollection MakeNonBlocking(this IServiceCollection services) => services;
}
