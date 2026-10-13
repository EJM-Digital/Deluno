namespace Deluno.Tray;

/// <summary>The web host builder the tray and the Windows service both use, and the logging it comes with (#1258).</summary>
internal static class TrayLogging
{
    internal static WebApplicationBuilder CreateBuilder(WebApplicationOptions options)
        => WebApplication.CreateBuilder(options);
}
