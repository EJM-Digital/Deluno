using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Deluno.Api;
using Deluno.Api.Backup;
using Deluno.Api.Downloads;
using Deluno.Api.Monitoring;
using Deluno.Contracts;
using Deluno.Filesystem;
using Deluno.Infrastructure;
using Deluno.Infrastructure.Observability;
using Deluno.Integrations;
using Deluno.Intake;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Metadata;
using Deluno.Integrations.Search;
using Deluno.Jobs;
using Deluno.Libraries;
using Deluno.Movies;
using Deluno.Connections;
using Deluno.Notifications;
using Deluno.Platform;
using Deluno.Quality;
using Deluno.Recovery;
using Deluno.Security;
using Deluno.Security.Hardening;
using Deluno.Realtime;
using Deluno.Series;
using Deluno.Worker;
using Deluno.Host;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.OpenApi.Models;
using System.Diagnostics;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
});

// Keep the normal local port while allowing isolated browser tests and packaged
// side-by-side diagnostics to select a different loopback endpoint.
var listenPort = builder.Configuration.GetValue<int?>("Server:Port") ?? 5099;

// Loopback only unless someone deliberately opts in. Deluno is a single-user
// local control plane, and ListenAnyIP put the whole API — including the
// unauthenticated setup endpoints on a fresh install — on every interface.
var allowLan = builder.Configuration.GetValue<bool>("Server:AllowLan");
builder.WebHost.ConfigureKestrel(options =>
{
    if (allowLan)
    {
        options.ListenAnyIP(listenPort);
    }
    else
    {
        options.ListenLocalhost(listenPort);
    }
});

builder.Services.AddDelunoInfrastructure(builder.Configuration);
builder.Services.AddDelunoApi();

// Login is the one endpoint an unauthenticated caller can hit repeatedly, and
// each attempt costs 100k PBKDF2 iterations — so it is both a credential
// guessing surface and a cheap way to burn the CPU of a machine that is
// meant to be transcoding. Fixed window, keyed by remote address.
// Configurable rather than hardcoded: the defaults are the production posture,
// but the smoke suite authenticates once per test from a single address and
// would otherwise throttle itself. Raised via Security:Login:* in that run.
var loginPermitLimit = builder.Configuration.GetValue<int?>("Security:Login:PermitLimit") ?? 10;
var loginWindowSeconds = builder.Configuration.GetValue<int?>("Security:Login:WindowSeconds") ?? 60;

// A global budget for the other ~279 routes an API key can drive. Login keeps
// its own stricter policy on top — a global limiter runs in addition to
// endpoint-specific policies, it does not replace them.
//
// The web app itself is one caller of this budget: every tab shares its
// session's bearer token, so N open tabs share one partition, not one each.
// #132 measured the dashboard alone at 204 requests/minute from a single idle
// tab, before the shell-wide 45s attention poll that runs on every
// authenticated page. Two dashboard tabs already approach 600/min; three
// exceed it. 3000/min (50 req/s sustained) leaves roughly 14x that single-tab
// headroom — comfortably covering several open tabs/windows of the same
// login while #132's polling reduction is still outstanding — and still
// firmly bounds the failure mode this exists for: a script or integration
// bug sustaining tens of requests per second is not something a real user
// does by opening tabs.
var apiPermitLimit = builder.Configuration.GetValue<int?>("Security:Api:PermitLimit") ?? ApiRateLimitDefaults.DefaultPermitLimit;
var apiWindowSeconds = builder.Configuration.GetValue<int?>("Security:Api:WindowSeconds") ?? ApiRateLimitDefaults.DefaultWindowSeconds;

builder.Services.AddRateLimiter(options =>
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
        // long-lived and a limiter would cut live updates, not abuse.
        // Artwork is excluded from auth and the no-store cache header for the
        // same reason it is excluded here — a poster grid fires dozens of
        // requests at once and none of them are attacker-controlled traffic.
        if (!path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWithSegments("/api/metadata/artwork", StringComparison.OrdinalIgnoreCase))
        {
            return RateLimitPartition.GetNoLimiter("exempt");
        }

        // Only requests presenting a generated API key are limited. Deluno's
        // own UI (and its background jobs, which never call this HTTP
        // surface at all) are trusted internal traffic — this limiter exists
        // for the third-party-script surface #142 was filed about, not to
        // throttle the app using itself.
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
builder.Services.AddDelunoSecurityModule();
builder.Services.AddDelunoNotificationsModule();
builder.Services.AddDelunoIntakeModule();
builder.Services.AddDelunoPlatformModule();
builder.Services.AddDelunoQualityModule();
builder.Services.AddDelunoConnectionsModule();
builder.Services.AddDelunoLibrariesModule();
builder.Services.AddDelunoMoviesModule();
builder.Services.AddDelunoSeriesModule();
builder.Services.AddDelunoJobsModule();
builder.Services.AddDelunoRecoveryModule();
builder.Services.AddDelunoIntegrationsModule();
builder.Services.AddDelunoFilesystemModule();
builder.Services.AddDelunoRealtimeModule();
builder.Services.AddDelunoWorkerModule();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    // Minimal-API request records may share the same nested type name across
    // modules (for example Movies.MetadataLinkRequest and Series.MetadataLinkRequest).
    // Use a stable fully-qualified schema id so the generated API document stays
    // available instead of failing on those valid independent contracts.
    options.CustomSchemaIds(type => (type.FullName ?? type.Name).Replace('+', '.'));
    options.SwaggerDoc(DelunoApiVersion.Current, new OpenApiInfo
    {
        Title = "Deluno API",
        Version = DelunoApiVersion.Current,
        Description = "Deluno operational API for local automation, integrations, and UI orchestration."
    });
});

var configuredDataRoot = builder.Configuration["Storage:DataRoot"];
var dataRoot = Path.GetFullPath(
    string.IsNullOrWhiteSpace(configuredDataRoot) ? "data" : configuredDataRoot,
    builder.Environment.ContentRootPath);
builder.Services
    .AddDataProtection()
    .SetApplicationName("Deluno")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataRoot, "protection-keys")));

// ISecretProtector pipeline. Backend is selected at first resolution
// (Windows → DPAPI, Linux/macOS → AES-GCM with master key from
// DELUNO_MASTER_KEY env var or <dataRoot>/secrets/master.key file).
// Legacy DataProtection reads continue to work via the composite reader.
builder.Services.AddDelunoPlatformSecrets(
    Path.Combine(dataRoot, "secrets", "master.key"));

var app = builder.Build();

app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        context.Response.StatusCode = 500;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync("{\"error\":\"An unexpected error occurred.\"}");
    });
});

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["X-XSS-Protection"] = "1; mode=block";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    if (!app.Environment.IsDevelopment())
    {
        context.Response.Headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
    }
    await next();
});

app.UseDelunoApiVersioning();

// Explicit, rather than relying on WebApplication's implicit UseRouting.
// Implicit routing matches at the very start of the pipeline — before any
// custom middleware — so it would resolve the endpoint against the
// unrewritten /api/v1/... path and the version alias above would have no
// effect on dispatch even though it edited the path.
app.UseRouting();

app.UseRateLimiter();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseDelunoApiUnmatchedPathGuard();
app.UseDelunoCorrelation();
app.UseAuthentication();
app.UseAuthorization();
app.Use(async (context, next) =>
{
    var path = context.Request.Path;

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
    var track = context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase);
    if (!track)
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

app.UseSwagger(options =>
{
    options.RouteTemplate = "api/openapi/{documentName}.json";
});
app.UseSwaggerUI(options =>
{
    options.RoutePrefix = "api/docs";
    options.SwaggerEndpoint($"/api/openapi/{DelunoApiVersion.Current}.json", $"Deluno API {DelunoApiVersion.Current}");
    options.DocumentTitle = "Deluno API docs";
});

app.MapDelunoApplicationEndpoints();
app.MapFallback(async context =>
{
    if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    var indexPath = Path.Combine(
        app.Environment.WebRootPath ?? app.Environment.ContentRootPath,
        "index.html");

    // SendFileAsync does not infer a content type, and Deluno sends
    // X-Content-Type-Options: nosniff, so without this the browser is told not to
    // guess and renders index.html as plain text. Every client-side route comes
    // through here, so the whole app looks like raw source until it is set.
    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.SendFileAsync(indexPath);
});

app.Run();
