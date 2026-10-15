using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Deluno.Security.Hardening;

public static class DelunoAllowedHostsMiddlewareExtensions
{
    /// <summary>
    /// Placed ahead of routing so it covers everything: the UI, the API,
    /// SignalR/websocket upgrades and the external integration API (#939).
    /// </summary>
    public static IApplicationBuilder UseDelunoAllowedHosts(this IApplicationBuilder app)
        => app.UseMiddleware<DelunoAllowedHostsMiddleware>();

    /// <summary>
    /// Records the host a reverse proxy or Docker host names in <c>X-Forwarded-Host</c>. It must sit ahead of
    /// <c>UseForwardedHeaders</c>, which consumes that header (it moves it to <c>X-Original-Host</c>), so a middleware
    /// placed after it never sees the header and the host is never offered as a candidate (#1341).
    /// </summary>
    public static IApplicationBuilder UseDelunoRecordForwardedHost(this IApplicationBuilder app)
        => app.Use((context, next) =>
        {
            var forwardedHost = context.Request.Headers["X-Forwarded-Host"].ToString();
            if (!string.IsNullOrWhiteSpace(forwardedHost))
            {
                context.RequestServices.GetRequiredService<IForwardedHostTracker>().Record(forwardedHost.Split(',')[0]);
            }

            return next(context);
        });
}

/// <summary>
/// Enforces Allowed Hosts (#939) ahead of routing, so it covers the UI, the
/// API, SignalR/websocket upgrades and the external integration API alike --
/// every one of them reaches Deluno through this same pipeline. Off (the
/// default, #936) does exactly what every earlier release did: answer
/// whatever hostname the request named.
/// </summary>
public sealed class DelunoAllowedHostsMiddleware(
    RequestDelegate next,
    IAllowedHostsSettingsCache settingsCache,
    IForwardedHostTracker forwardedHostTracker,
    ILogger<DelunoAllowedHostsMiddleware> logger)
{
    private static readonly ConcurrentDictionary<string, byte> LoggedRefusals = new(StringComparer.OrdinalIgnoreCase);

    public async Task InvokeAsync(HttpContext context)
    {
        // Recorded whether or not enforcement is on, so a reverse proxy or
        // Docker host shows up as a candidate to tick on the Security page
        // well before anyone turns Allowed Hosts on.
        var forwardedHost = context.Request.Headers["X-Forwarded-Host"].ToString();
        if (!string.IsNullOrWhiteSpace(forwardedHost))
        {
            forwardedHostTracker.Record(forwardedHost.Split(',')[0]);
        }

        var settings = await settingsCache.GetAsync(context.RequestAborted);
        if (!settings.Enabled)
        {
            await next(context);
            return;
        }

        var host = context.Request.Host.Host;
        if (AllowedHostMatcher.IsAlwaysAllowedLoopbackHost(host) || AllowedHostMatcher.Matches(host, settings.AllowedHosts))
        {
            await next(context);
            return;
        }

        if (LoggedRefusals.TryAdd(host, 0))
        {
            logger.LogWarning(
                "Deluno refused a request addressed to host '{Host}' because Allowed Hosts does not include it.",
                host);
        }

        await WriteRefusalAsync(context, host);
    }

    private static async Task WriteRefusalAsync(HttpContext context, string host)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(RefusalPage(host), context.RequestAborted);
    }

    private static string RefusalPage(string host) =>
        """
        <!DOCTYPE html>
        <html lang="en">
        <head>
            <meta charset="utf-8" />
            <title>Deluno refused this address</title>
            <meta name="viewport" content="width=device-width, initial-scale=1" />
            <style>
                body { font-family: system-ui, sans-serif; background: #0f1115; color: #e7e9ee; display: flex; min-height: 100vh; align-items: center; justify-content: center; margin: 0; padding: 24px; }
                main { max-width: 30rem; }
                h1 { font-size: 1.25rem; margin-bottom: 0.75rem; }
                code { background: #1c1f27; border-radius: 6px; padding: 0.1rem 0.4rem; }
                p { line-height: 1.5; }
            </style>
        </head>
        <body>
            <main>
                <h1>Deluno does not answer on this address</h1>
                <p>The request was sent to <code>__HOST__</code>, which is not on Deluno's Allowed Hosts list.</p>
                <p>If this address should work, sign in on an address that is already allowed and add it from System &rarr; Security. localhost on this machine always works.</p>
            </main>
        </body>
        </html>
        """.Replace("__HOST__", System.Net.WebUtility.HtmlEncode(host));
}
