using Deluno.Api;
using Deluno.Connections;
using Deluno.Filesystem;
using Deluno.Host;
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
using Deluno.Worker;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Platform.Tests.Routing;

public sealed class EndpointInventoryTests
{
    [Fact]
    public void Application_route_inventory_matches_snapshot()
    {
        using var app = BuildApplication();

        var actual = ApplicationEndpoints(app)
            .Select(endpoint =>
            {
                var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [];
                return $"{string.Join(',', methods.Order(StringComparer.Ordinal))} {endpoint.RoutePattern.RawText}";
            })
            .Order(StringComparer.Ordinal)
            .ToArray();

        var snapshotPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../../tests/Deluno.Platform.Tests/Routing/endpoint-inventory.snapshot.txt"));
        var expected = File.ReadAllLines(snapshotPath);

        Assert.Equal(expected, actual);
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
            Path.Combine(Path.GetTempPath(), "deluno-endpoint-inventory", "master.key"));
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
