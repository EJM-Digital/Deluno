using Deluno.Api;
using Deluno.Connections;
using Deluno.Filesystem;
using Deluno.Worker;
using Deluno.Infrastructure;
using Deluno.Integrations;
using Deluno.Intake;
using Deluno.Jobs;
using Deluno.Libraries;
using Deluno.Movies;
using Deluno.Notifications;
using Deluno.Platform;
using Deluno.Quality;
using Deluno.Recovery;
using Deluno.Realtime;
using Deluno.Security;
using Deluno.Series;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using System.IO;

namespace Deluno.Platform.Tests.Routing;

/// <summary>
/// Keeps the copy-paste automation recipes tied to real shipped routes. The
/// /api/v1 prefix is an API-version alias applied by middleware, so the route
/// inventory intentionally records the underlying /api path.
/// </summary>
public sealed class AutomationRecipeContractTests
{
    [Fact]
    public void Documented_automation_recipes_have_real_versioned_route_targets()
    {
        using var app = BuildApplication();
        var routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .SelectMany(endpoint =>
            {
                var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [];
                return methods.Select(method => $"{method} {endpoint.RoutePattern.RawText}");
            })
            .ToHashSet(StringComparer.Ordinal);

        var documented = new[]
        {
            "GET /api/health/ready",
            "GET /api/api-keys/scope-templates",
            "POST /api/automation/catalogue/bulk",
            "POST /api/automation/series/{seriesId}/episodes/bulk",
            "GET /api/automation/summary",
            "GET /api/notification-webhooks/deliveries",
            "POST /api/notification-webhooks/deliveries/{deliveryId}/replay",
            "POST /api/libraries/{id}/search-now",
            "POST /api/libraries/{id}/import-existing",
            "GET /api/libraries/{id}/import-existing",
            "GET /api/libraries/{id}/import-existing/issues",
            "POST /api/libraries/{id}/import-existing/pause",
            "POST /api/libraries/{id}/import-existing/resume",
            "POST /api/intake-sources/{id}/approve-preview",
            "PUT /api/settings/automation",
            "GET /api/jobs",
            "GET /api/activity",
            "GET /api/decisions",
            "GET /api/backups/",
            "POST /api/backups/"
        };

        Assert.True(
            documented.All(routes.Contains),
            $"Missing documented automation routes: {string.Join(", ", documented.Where(route => !routes.Contains(route)))}");

        var repositoryRoot = new DirectoryInfo(AppContext.BaseDirectory);
        while (repositoryRoot is not null && !File.Exists(Path.Combine(repositoryRoot.FullName, "Deluno.slnx")))
        {
            repositoryRoot = repositoryRoot.Parent;
        }

        Assert.NotNull(repositoryRoot);
        var homeAssistant = File.ReadAllText(Path.Combine(
            repositoryRoot!.FullName,
            "integrations",
            "home-assistant",
            "deluno.yaml"));
        Assert.Contains("deluno_pause_automation:", homeAssistant, StringComparison.Ordinal);
        Assert.Contains("deluno_resume_automation:", homeAssistant, StringComparison.Ordinal);
        Assert.Contains("/api/v1/settings/automation", homeAssistant, StringComparison.Ordinal);
    }

    private static WebApplication BuildApplication()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddDelunoInfrastructure(builder.Configuration);
        builder.Services.AddDelunoApi();
        builder.Services.AddDelunoSecurityModule();
        builder.Services.AddDelunoNotificationsModule();
        builder.Services.AddDelunoIntakeModule();
        builder.Services.AddDelunoPlatformModule();
        builder.Services.AddDelunoPlatformSecrets(
            Path.Combine(Path.GetTempPath(), "deluno-automation-contract", "master.key"));
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

        var app = builder.Build();
        app.MapDelunoApplicationEndpoints();
        return app;
    }
}
