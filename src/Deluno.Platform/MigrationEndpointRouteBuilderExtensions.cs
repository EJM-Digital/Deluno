using Microsoft.AspNetCore.Mvc;
using Deluno.Contracts;
using Deluno.Quality.Presets;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Deluno.Infrastructure.Observability;
using Deluno.Infrastructure.Resilience;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Platform.Migration;
using Deluno.Platform.Processing;
using Deluno.Quality;
using Deluno.Notifications;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Security.Contracts;
using Deluno.Security;
using System.Net.Http;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Deluno.Quality.Contracts;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;

namespace Deluno.Platform;

public static class MigrationEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapDelunoMigrationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var write = endpoints.MapGroup(string.Empty)
            .RequireAuthorization(DelunoAuthorizationPolicies.Write);

        var migration = write.MapGroup("/api/migration");

        migration.MapPost("/preview", async (
            HttpContext httpContext,
            [FromBody] MigrationImportRequest request,
            IMigrationAssistantService migrationAssistant,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var report = await migrationAssistant.PreviewAsync(request, cancellationToken);
            return Results.Ok(report);
        });

        migration.MapPost("/apply", async (
            HttpContext httpContext,
            [FromBody] MigrationImportRequest request,
            IMigrationAssistantService migrationAssistant,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var result = await migrationAssistant.ApplyAsync(request, cancellationToken);
            return result.Report.Valid ? Results.Ok(result) : Results.BadRequest(result.Report);
        });

        migration.MapGet("/reports", async (
            HttpContext httpContext,
            IMigrationAuditRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            return denied ?? Results.Ok(await repository.ListMigrationAuditReportsAsync(20, cancellationToken));
        });

        migration.MapGet("/reports/{id}", async (
            string id,
            HttpContext httpContext,
            IMigrationAuditRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var report = await repository.GetMigrationAuditReportAsync(id, cancellationToken);
            return report is null ? Results.NotFound() : Results.Ok(report);
        });

        return endpoints;
    }

}
