using Deluno.Api;
using Deluno.Api.Backup;
using Deluno.Api.Downloads;
using Deluno.Api.ImportRecovery;
using Deluno.Connections;
using Deluno.Filesystem;
using Deluno.Infrastructure;
using Deluno.Integrations;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Metadata;
using Deluno.Integrations.Search;
using Deluno.Intake;
using Deluno.Host;
using Deluno.Jobs;
using Deluno.Libraries;
using Deluno.Movies;
using Deluno.Notifications;
using Deluno.Platform;
using Deluno.Quality;
using Deluno.Realtime;
using Deluno.Security;
using Deluno.Security.Hardening;
using Deluno.Series;
using Deluno.Worker;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Security;

public sealed class EndpointAuthorizationCoverageTests
{
    [Fact]
    public void Every_application_endpoint_declares_authorization_or_is_explicitly_public()
    {
        using var app = BuildApplication();

        var uncovered = ApplicationEndpoints(app)
            .Where(endpoint => endpoint.Metadata.GetMetadata<IAuthorizeData>() is null &&
                               endpoint.Metadata.GetMetadata<DelunoPublicEndpointAttribute>() is null)
            .Select(endpoint => endpoint.RoutePattern.RawText!)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(uncovered);
    }

    [Fact]
    public void Public_application_endpoints_are_an_intentionally_small_exact_set()
    {
        using var app = BuildApplication();

        var publicRoutes = ApplicationEndpoints(app)
            .Where(endpoint => endpoint.Metadata.GetMetadata<DelunoPublicEndpointAttribute>() is not null)
            .Select(endpoint => endpoint.RoutePattern.RawText!)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "/api/auth/bootstrap",
                "/api/auth/bootstrap-status",
                "/api/auth/login",
                "/api/health/live",
                "/api/health/ready",
                "/api/metadata/artwork/{cacheKey}",
                "/health"
            ],
            publicRoutes);
    }

    private static RouteEndpoint[] ApplicationEndpoints(WebApplication app)
        => ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText is { } path &&
                               (path.StartsWith("/api", StringComparison.Ordinal) ||
                                path.StartsWith("/hubs", StringComparison.Ordinal) ||
                                path == "/health" ||
                                path.StartsWith("/monitoring", StringComparison.Ordinal)))
            .ToArray();

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
            Path.Combine(Path.GetTempPath(), "deluno-endpoint-coverage", "master.key"));
        builder.Services.AddDelunoQualityModule();
        builder.Services.AddDelunoConnectionsModule();
        builder.Services.AddDelunoLibrariesModule();
        builder.Services.AddDelunoMoviesModule();
        builder.Services.AddDelunoSeriesModule();
        builder.Services.AddDelunoJobsModule();
        builder.Services.AddDelunoIntegrationsModule();
        builder.Services.AddScoped<TmdbMetadataProvider>();
        builder.Services.AddDelunoFilesystemModule();
        builder.Services.AddDelunoRealtimeModule();
        builder.Services.AddDelunoWorkerModule();

        var app = builder.Build();
        app.MapDelunoApplicationEndpoints();

        return app;
    }
}
