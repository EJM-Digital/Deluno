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
        // Set DELUNO_UPDATE_ROUTE_SNAPSHOT=1 to rewrite the snapshot after a
        // deliberate route change, then review the diff before committing.
        if (Environment.GetEnvironmentVariable("DELUNO_UPDATE_ROUTE_SNAPSHOT") == "1")
        {
            File.WriteAllLines(snapshotPath, actual);
        }

        var expected = File.ReadAllLines(snapshotPath);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void No_route_returns_a_download_clients_login()
    {
        // #1142: the person picks the Usenet logins at install, so Deluno never shows a stored one (#1061).
        using var app = BuildApplication();

        Assert.DoesNotContain(ApplicationEndpoints(app), endpoint =>
            endpoint.RoutePattern.RawText!.Contains("reveal-login", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// #1296: the TypeScript SDK (sdk/typescript/src/index.ts) calls only routes the shipped server has, with the method it uses. The route
    /// table is the real one, built from the same modules the host maps, not the snapshot file; each call the SDK makes is read from its
    /// source and must match an endpoint of the same method (a route parameter matches whatever name the server gives it).
    /// </summary>
    [Fact]
    public void Every_route_the_typescript_sdk_calls_is_a_route_the_server_has_with_the_method_the_sdk_uses()
    {
        using var app = BuildApplication();
        var served = ApplicationEndpoints(app)
            .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [])
                .Select(method => (Method: method, Segments: Segments(endpoint.RoutePattern.RawText!))))
            .ToArray();

        var source = File.ReadAllText(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "../../../../../sdk/typescript/src/index.ts")));
        var calls = System.Text.RegularExpressions.Regex.Matches(
            source, @"this\.request(?:<[^>]*>)?\(\s*(?:""(?<q>[^""]+)""|`(?<t>[^`]+)`)(?:\s*,\s*\{(?<opts>[^}]*))?");
        Assert.True(calls.Count >= 20, $"expected the SDK's calls to be read from its source, found {calls.Count}");

        var missing = new List<string>();
        foreach (System.Text.RegularExpressions.Match call in calls)
        {
            var template = call.Groups["q"].Success ? call.Groups["q"].Value : call.Groups["t"].Value;
            var method = System.Text.RegularExpressions.Regex.Match(call.Groups["opts"].Value, @"method:\s*""(?<m>[A-Z]+)""") is { Success: true } m
                ? m.Groups["m"].Value
                : "GET";
            // The SDK's query string and its encodeURIComponent(...) parts: only the path and its parameters are compared.
            var path = template.Split('?')[0];
            path = System.Text.RegularExpressions.Regex.Replace(path, @"\$\{encodeURIComponent\([^)]*\)\}", "{}");
            path = System.Text.RegularExpressions.Regex.Replace(path, @"\$\{[^}]*\}", string.Empty);
            // /api/v1/x is an alias of /api/x (ApiVersioning), except for the few areas that have their own versioned route.
            var dedicated = new[] { "download-dispatches", "import-resolutions", "dispatch-metrics", "release-preferences", "guides" };
            var versioned = Segments("/api/v1" + path);
            var wanted = dedicated.Contains(versioned.ElementAtOrDefault(2)) ? versioned : versioned.Where((_, index) => index != 1).ToArray();

            if (!served.Any(route => route.Method == method && route.Segments.Length == wanted.Length &&
                                     route.Segments.Zip(wanted).All(pair => pair.First == "{}" || pair.First == pair.Second)))
            {
                missing.Add($"{method} /api/v1{path}");
            }
        }

        Assert.True(missing.Count == 0, "The SDK calls routes the server does not have: " + string.Join("; ", missing));
    }

    private static string[] Segments(string route)
        => route.Trim('/').Split('/').Select(segment => segment.StartsWith('{') ? "{}" : segment).ToArray();

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
