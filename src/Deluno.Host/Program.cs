using Deluno.Api;
using Deluno.Api.Backup;
using Deluno.Filesystem;
using Deluno.Infrastructure;
using Deluno.Infrastructure.Observability;
using Deluno.Integrations;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Metadata;
using Deluno.Jobs;
using Deluno.Movies;
using Deluno.Platform;
using Deluno.Realtime;
using Deluno.Series;
using Deluno.Worker;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
});

builder.Services.AddDelunoInfrastructure(builder.Configuration);
builder.Services.AddDelunoApi();
builder.Services.AddDelunoPlatformModule();
builder.Services.AddDelunoMoviesModule();
builder.Services.AddDelunoSeriesModule();
builder.Services.AddDelunoJobsModule();
builder.Services.AddDelunoIntegrationsModule();
builder.Services.AddDelunoFilesystemModule();
builder.Services.AddDelunoRealtimeModule();
builder.Services.AddDelunoWorkerModule();

var configuredDataRoot = builder.Configuration["Storage:DataRoot"];
var dataRoot = Path.GetFullPath(
    string.IsNullOrWhiteSpace(configuredDataRoot) ? "data" : configuredDataRoot,
    builder.Environment.ContentRootPath);
builder.Services
    .AddDataProtection()
    .SetApplicationName("Deluno")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataRoot, "protection-keys")));

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseDelunoCorrelation();
app.Use(async (context, next) =>
{
    var path = context.Request.Path;

    var requiresAuthentication =
        (path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) &&
         !path.Equals("/api/auth/login", StringComparison.OrdinalIgnoreCase) &&
         !path.Equals("/api/auth/bootstrap-status", StringComparison.OrdinalIgnoreCase) &&
         !path.Equals("/api/auth/bootstrap", StringComparison.OrdinalIgnoreCase) &&
         !path.StartsWithSegments("/api/health", StringComparison.OrdinalIgnoreCase)) ||
        path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase);

    if (!requiresAuthentication)
    {
        await next();
        return;
    }

    var repository = context.RequestServices.GetRequiredService<Deluno.Platform.Data.IPlatformSettingsRepository>();
    var denied = await UserAuthorization.RequireAuthenticatedAsync(context, repository, context.RequestAborted);
    if (denied is not null)
    {
        await denied.ExecuteAsync(context);
        return;
    }

    var scopeDenied = UserAuthorization.RequireApiScope(context, ResolveRequiredApiScopes(path, context.Request.Method));
    if (scopeDenied is not null)
    {
        await scopeDenied.ExecuteAsync(context);
        return;
    }

    await next();
});

app.MapDelunoApi();
app.MapDelunoBackupEndpoints();
app.MapDelunoPlatformEndpoints();
app.MapDelunoMoviesEndpoints();
app.MapDelunoSeriesEndpoints();
app.MapDelunoJobsEndpoints();
app.MapDelunoDownloadClientIntegrationEndpoints();
app.MapDelunoMetadataEndpoints();
app.MapDelunoFilesystemEndpoints();
app.MapDelunoRealtime();
app.MapFallbackToFile("index.html");

app.Run();

static string[] ResolveRequiredApiScopes(PathString path, string method)
{
    var isRead = HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);

    if (isRead)
    {
        return ["read"];
    }

    if (path.StartsWithSegments("/api/download-clients", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWithSegments("/api/download-dispatches", StringComparison.OrdinalIgnoreCase))
    {
        return ["queue"];
    }

    if (path.StartsWithSegments("/api/filesystem/import", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWithSegments("/api/integrations", StringComparison.OrdinalIgnoreCase))
    {
        return ["imports", "queue"];
    }

    if (path.StartsWithSegments("/api/backups", StringComparison.OrdinalIgnoreCase))
    {
        return ["system"];
    }

    return ["write"];
}
