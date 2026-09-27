using Deluno.Infrastructure.Observability;
using Deluno.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Deluno.Api.SystemInfo;

public sealed record LogLevelResponse(string Level);

public sealed record SetLogLevelRequest(string? Level);

/// <summary>
/// A runtime-configurable Log Level in Settings (#866), matching Radarr's
/// own Info/Debug/Trace dropdown: changeable while Deluno is running, and
/// persisted the same way appsettings.json's own starting level is.
/// </summary>
public static class SystemLogLevelEndpointRouteBuilderExtensions
{
    private static readonly LogLevel[] AllowedLevels =
    [
        LogLevel.Trace,
        LogLevel.Debug,
        LogLevel.Information,
        LogLevel.Warning,
        LogLevel.Error
    ];

    public static IEndpointRouteBuilder MapDelunoSystemLogLevelEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("/system/log-level", (IRuntimeLogLevelStore store)
                => Results.Ok(new LogLevelResponse(store.Current.ToString())))
            .RequireAuthorization(DelunoAuthorizationPolicies.Read);

        api.MapPut("/system/log-level", async (
                SetLogLevelRequest request,
                IRuntimeLogLevelStore store,
                CancellationToken cancellationToken) =>
        {
            if (!Enum.TryParse<LogLevel>(request.Level, ignoreCase: true, out var level) || !AllowedLevels.Contains(level))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["level"] = ["Choose Trace, Debug, Information, Warning, or Error."]
                });
            }

            await store.SetAsync(level, cancellationToken);
            return Results.Ok(new LogLevelResponse(store.Current.ToString()));
        })
            .RequireAuthorization(DelunoAuthorizationPolicies.System);

        return api;
    }
}
