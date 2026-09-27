using System.Diagnostics;
using System.Runtime.InteropServices;
using Deluno.Infrastructure.Storage;
using Deluno.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace Deluno.Api.SystemInfo;

public sealed record SystemInstallationInfoResponse(
    DateTimeOffset ProcessStartUtc,
    long UptimeSeconds,
    string OsDescription,
    string RuntimeVersion,
    string DatabaseType,
    string DatabaseLocation,
    string AppDataPath);

/// <summary>
/// Uptime, runtime, and app-data details for the System screen's "This
/// installation" card (#869), read live rather than hardcoded at build
/// time -- the same gap Radarr's System > Status > About panel closes.
/// </summary>
public static class SystemInstallationInfoEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapDelunoSystemInstallationInfoEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("/system/info", (
                IOptions<StoragePathOptions> storage,
                TimeProvider timeProvider) =>
        {
            var process = Process.GetCurrentProcess();
            var startUtc = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
            var dataRoot = Path.GetFullPath(storage.Value.DataRoot);

            return Results.Ok(new SystemInstallationInfoResponse(
                ProcessStartUtc: startUtc,
                UptimeSeconds: (long)Math.Max(0, (timeProvider.GetUtcNow() - startUtc).TotalSeconds),
                OsDescription: RuntimeInformation.OSDescription,
                RuntimeVersion: RuntimeInformation.FrameworkDescription,
                DatabaseType: "SQLite",
                DatabaseLocation: dataRoot,
                AppDataPath: dataRoot));
        })
            .RequireAuthorization(DelunoAuthorizationPolicies.Read);

        return api;
    }
}
