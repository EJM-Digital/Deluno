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
    string AppDataPath,
    string HostName,
    bool HostLooksLikeContainerId);

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
                TimeProvider timeProvider) => Results.Ok(Describe(storage.Value.DataRoot, timeProvider)))
            .RequireAuthorization(DelunoAuthorizationPolicies.Read);

        return api;
    }

    /// <summary>
    /// The one reading of "how long has Deluno been running, on what, storing
    /// where", shared by <c>/api/system/info</c> and the combined
    /// <c>/api/system/status</c> (#1020) so the two cannot disagree.
    /// </summary>
    public static SystemInstallationInfoResponse Describe(string dataRootPath, TimeProvider timeProvider)
    {
        var process = Process.GetCurrentProcess();
        var startUtc = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        var dataRoot = Path.GetFullPath(dataRootPath);

        return new SystemInstallationInfoResponse(
            ProcessStartUtc: startUtc,
            UptimeSeconds: (long)Math.Max(0, (timeProvider.GetUtcNow() - startUtc).TotalSeconds),
            OsDescription: RuntimeInformation.OSDescription,
            RuntimeVersion: RuntimeInformation.FrameworkDescription,
            DatabaseType: "SQLite",
            DatabaseLocation: dataRoot,
            AppDataPath: dataRoot,
            HostName: Deluno.Contracts.DelunoHostName.Current,
            HostLooksLikeContainerId: Deluno.Contracts.DelunoHostName.CurrentLooksLikeContainerId);
    }
}
