using System.Diagnostics;
using System.Threading.RateLimiting;
using Deluno.Api;
using Deluno.Api.Monitoring;
using Deluno.Contracts;
using Deluno.Infrastructure.Observability;
using Deluno.Infrastructure.Storage;
using Deluno.Realtime;
using Deluno.Security;
using Deluno.Security.Hardening;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Deluno.Worker;

/// <summary>
/// What the hosts are allowed to do differently in the shared request pipeline.
/// Everything else is the same for every host, on purpose.
/// </summary>
public sealed record DelunoPipelineOptions
{
    /// <summary>
    /// The scopes a request needs, by path and method. The sign-in gate in
    /// front of every API route uses it. Defaults to <see cref="DelunoRequestPipeline.ResolveApiScopes"/>.
    /// </summary>
    public Func<PathString, string, string[]> ResolveScopes { get; init; } = DelunoRequestPipeline.ResolveApiScopes;

    /// <summary>
    /// Runs after the request-timing step and before the sign-in gate: for
    /// documentation endpoints that answer the request themselves and stay
    /// open, such as the API docs of the container build.
    /// </summary>
    public Action<IApplicationBuilder>? BeforeSignInGate { get; init; }
}

/// <summary>
/// The request pipeline of every Deluno host, built once (#1176).
///
/// <para><b>Why one place.</b> Deluno.Host, the Windows app's own web server
/// and the Windows service each wrote their own pipeline and they drifted. The
/// rc.30 fix that added an explicit <c>UseRouting()</c> to the Windows app
/// switched off the authentication and authorization steps
/// <c>WebApplication</c> adds by itself, and every endpoint with
/// authorization metadata answered 500 on the installed build while every test
/// passed. A host now adds its services with
/// <see cref="AddDelunoWebPipeline"/> and its middleware with
/// <see cref="UseDelunoRequestPipeline"/>, and differs only by the options it
/// passes.</para>
/// </summary>
public static class DelunoRequestPipeline
{
    /// <summary>The scopes an API request needs by default: reads read, the rest write, a few areas their own.</summary>
    public static string[] ResolveApiScopes(PathString path, string method)
    {
        var isRead = HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);
        if (isRead) return ["read"];
        // #1333: the docs give queue removal the queue scope. A queue-only key is let through to the route.
        if (path.StartsWithSegments("/api/queue", StringComparison.OrdinalIgnoreCase))
            return ["queue"];
        // #1333: a command names its own scope in the docs (write, queue or system). The gate lets any of them reach the command
        // route, and the route then asks for the scope of the command named in the body.
        if (path.Equals("/api/command", StringComparison.OrdinalIgnoreCase))
            return ["write", "queue", "system"];
        if (path.StartsWithSegments("/api/download-clients", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWithSegments("/api/download-dispatches", StringComparison.OrdinalIgnoreCase))
            return ["queue"];
        if (path.StartsWithSegments("/api/filesystem/import", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWithSegments("/api/integrations", StringComparison.OrdinalIgnoreCase))
            return ["imports", "queue"];
        if (path.StartsWithSegments("/api/backups", StringComparison.OrdinalIgnoreCase))
            return ["system"];
        return ["write"];
    }

    /// <summary>
    /// Forwarded headers, response compression and the rate limiters. Allowed
    /// Hosts (#939) matches against the request host, so a reverse proxy or
    /// Docker host in front of Deluno needs its X-Forwarded-Host applied
    /// first. The known networks and proxies are cleared rather than left at
    /// their loopback-only default: Deluno is never exposed publicly without
    /// deliberately opting into LAN or Docker, and a proxy is as likely to run
    /// on another container's address as on loopback.
    /// </summary>
    public static IServiceCollection AddDelunoWebPipeline(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // #1279: RequestBodyLimits names the limit per route; the form reader must allow the largest of them (a backup).
        services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
            options.MultipartBodyLengthLimit = RequestBodyLimits.BackupFileBytes);

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
        });

        // Login is the one endpoint an unauthenticated caller can hit repeatedly, and
        // each attempt costs 100k PBKDF2 iterations. Configurable rather than hardcoded:
        // the defaults are the production posture, but the smoke suite authenticates
        // once per test from a single address and would otherwise throttle itself.
        var loginPermitLimit = configuration.GetValue<int?>("Security:Login:PermitLimit") ?? 10;
        var loginWindowSeconds = configuration.GetValue<int?>("Security:Login:WindowSeconds") ?? 60;

        // A global budget for the routes an API key can drive; login keeps its own
        // stricter policy on top. See ApiRateLimitDefaults for why it is generous.
        var apiPermitLimit = configuration.GetValue<int?>("Security:Api:PermitLimit") ?? ApiRateLimitDefaults.DefaultPermitLimit;
        var apiWindowSeconds = configuration.GetValue<int?>("Security:Api:WindowSeconds") ?? ApiRateLimitDefaults.DefaultWindowSeconds;

        services.AddDelunoResponseCompression();

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(DelunoRateLimitPolicies.Login, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = loginPermitLimit,
                        Window = TimeSpan.FromSeconds(loginWindowSeconds),
                        QueueLimit = 0
                    }));

            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
            {
                var path = httpContext.Request.Path;

                // Static files and the SignalR hubs are exempt: hub connections are
                // long-lived and a limiter would cut live updates, not abuse. Artwork
                // is exempt for the same reason it skips auth and the no-store header:
                // a poster grid fires dozens of requests at once.
                if (!path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWithSegments("/api/metadata/artwork", StringComparison.OrdinalIgnoreCase))
                {
                    return RateLimitPartition.GetNoLimiter("exempt");
                }

                // Only requests presenting a generated API key are limited. Deluno's own
                // UI is trusted internal traffic; this exists for the third-party-script
                // surface, not to throttle the app using itself.
                var partitionKey = ApiRateLimitPartitionKeyResolver.ResolveOrExempt(httpContext);
                if (partitionKey is null)
                {
                    return RateLimitPartition.GetNoLimiter("exempt-session");
                }

                return RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey,
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = apiPermitLimit,
                        Window = TimeSpan.FromSeconds(apiWindowSeconds),
                        QueueLimit = 0
                    });
            });

            options.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.Headers.RetryAfter =
                    apiWindowSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                context.HttpContext.Response.ContentType = "application/json";
                await context.HttpContext.Response.WriteAsync(
                    "{\"error\":\"Rate limit exceeded.\",\"retryAfterSeconds\":" + apiWindowSeconds + "}",
                    cancellationToken);
            };
        });

        return services;
    }

    /// <summary>
    /// The middleware, in the one order every host uses, ending with the
    /// endpoint map and the web app's fallback page.
    /// </summary>
    public static WebApplication UseDelunoRequestPipeline(this WebApplication app, DelunoPipelineOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        options ??= new DelunoPipelineOptions();

        app.UseExceptionHandler(errorApp =>
        {
            errorApp.Run(async context =>
            {
                context.Response.StatusCode = 500;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("{\"error\":\"An unexpected error occurred.\"}");
            });
        });

        var isDevelopment = app.Environment.IsDevelopment();
        app.Use(async (context, next) =>
        {
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["X-Frame-Options"] = "DENY";
            context.Response.Headers["X-XSS-Protection"] = "1; mode=block";
            context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            if (!isDevelopment)
            {
                context.Response.Headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
            }
            await next();
        });

        // #993: captures the real socket peer before UseForwardedHeaders rewrites
        // Connection.RemoteIpAddress from X-Forwarded-For, so "skip sign-in for
        // local devices" can judge the header against its own trusted-proxies list.
        app.UseDelunoCaptureOriginalRemoteAddress();
        app.UseForwardedHeaders();

        // Allowed Hosts (#939): refuses a request addressed to a host Deluno was not
        // told to answer on, before routing decides anything.
        app.UseDelunoAllowedHosts();

        app.UseDelunoApiVersioning();
        app.UseDelunoRealtimePosition();

        // Explicit, so the version alias above takes effect on dispatch and the
        // sign-in gate can see which endpoint a request is for. An explicit
        // UseRouting turns off the authentication and authorization steps
        // WebApplication adds by itself, which is why they are added below.
        app.UseRouting();

        // Kestrel can accept a request while hosted-service startup is still applying
        // database migrations. Hold normal UI/API traffic behind the schema gate so a
        // fresh install cannot run a search against a half-created catalogue.
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            var isHealthProbe = path.Equals("/health", StringComparison.OrdinalIgnoreCase) ||
                                path.Equals("/api/health/live", StringComparison.OrdinalIgnoreCase) ||
                                path.Equals("/api/health/ready", StringComparison.OrdinalIgnoreCase);

            if (!isHealthProbe)
            {
                var startupGate = context.RequestServices.GetRequiredService<IDelunoStartupGate>();
                try
                {
                    await startupGate.WaitAsync(TimeSpan.FromSeconds(30), context.RequestAborted);
                }
                catch (TimeoutException)
                {
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(
                        "{\"error\":\"Deluno is still completing database migrations. Try again shortly.\"}",
                        context.RequestAborted);
                    return;
                }
            }

            await next();
        });

        app.UseRateLimiter();
        // JSON and the UI bundle are text, so Brotli/Gzip at the fastest level cuts the
        // bytes sent for every screen (#1081). Live event streams are not compressible
        // MIME types, so SignalR and server-sent events pass through untouched.
        app.UseResponseCompression();
        app.UseDefaultFiles();
        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = DelunoStaticFileCaching.Apply
        });
        app.UseDelunoApiUnmatchedPathGuard();
        app.UseDelunoCorrelation();
        app.UseDelunoConfigChangedPush();
        app.UseDelunoAttentionChangedPush();
        app.UseAuthentication();
        app.UseAuthorization();

        app.Use(async (context, next) =>
        {
            var path = context.Request.Path;

            // Posters are long-cached files; no-store would stop the browser keeping them.
            if ((path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) &&
                 !path.StartsWithSegments("/api/metadata/artwork", StringComparison.OrdinalIgnoreCase)) ||
                path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.Headers.CacheControl = "no-store";
            }

            await next();
        });

        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
            {
                await next();
                return;
            }

            var started = Stopwatch.GetTimestamp();
            try
            {
                await next();
            }
            finally
            {
                var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                var tracker = context.RequestServices.GetRequiredService<IApiLatencyTracker>();
                tracker.Record(
                    context.Request.Path.HasValue ? context.Request.Path.Value! : "unknown",
                    elapsed,
                    context.Response.StatusCode);
            }
        });

        options.BeforeSignInGate?.Invoke(app);

        // The sign-in check in front of every API route (#1146); a route that
        // declares itself public (the poster route) is let through.
        app.UseDelunoSignInGate(options.ResolveScopes);

        // A request the framework cannot bind gets a plain message, after the sign-in (#1280).
        app.UseDelunoBadRequestAnswers();

        // #1279: how large a body each route takes, and how long each saved text member may be.
        app.UseDelunoRequestBodyLimits();

        app.MapDelunoApplicationEndpoints();

        app.MapFallback(async context =>
        {
            // An unknown API path is a 404, not the app shell: serving index.html
            // would make a broken client call look like a successful page load.
            if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var indexPath = Path.Combine(
                app.Environment.WebRootPath ?? app.Environment.ContentRootPath,
                "index.html");

            // SendFileAsync does not infer a content type and Deluno sends nosniff, so
            // without this the browser renders index.html as plain text.
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers.CacheControl = DelunoStaticFileCaching.HtmlCacheControl;
            await context.Response.SendFileAsync(indexPath);
        });

        return app;
    }
}
