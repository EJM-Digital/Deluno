using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Deluno.Persistence.Tests.Support;
using Deluno.Worker;
using Microsoft.AspNetCore.Http;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The Integration endpoints list on System > API tells a person which scope each route needs. This reads that list from the
/// screen's own source, finds every route in the running application's endpoint map, and checks that a key holding exactly the
/// listed scope gets in and one holding every other scope is refused. The openapi and docs addresses exist only on a Deluno.Host
/// build (the test host does not map them), and are the only listed routes allowed to be absent here.
/// </summary>
public sealed partial class TheEndpointListOnTheApiScreenNamesRealRoutesAndTheirScopeTests
{
    private static readonly string[] OnlyOnHostBuilds = ["/api/openapi/v1.json", "/api/docs"];

    // ["read", "GET", "/api/integrations/external/manifest"],
    [GeneratedRegex("""\["(?<scope>[a-z]+)", "(?<method>[A-Z|]+)", "(?<path>/api/[^"?\s]+)""")]
    private static partial Regex ListedRow();

    private static IReadOnlyList<(string Scope, string Method, string Path)> Listed()
    {
        var source = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../apps/web/src/routes/system-api-page.tsx")));
        return ListedRow().Matches(source)
            .SelectMany(match => match.Groups["method"].Value.Split('|').Select(method => (match.Groups["scope"].Value, method, match.Groups["path"].Value)))
            // The integration rows come from the table the request gate reads (#1410), not from the screen's source.
            .Concat(Deluno.Security.IntegrationRouteScopes.All.Where(route => route.Featured).Select(route => (route.Scope, route.Method, route.Path)))
            .Distinct()
            .ToArray();
    }

    [Fact]
    public void The_screen_lists_the_routes_to_check()
    {
        var rows = Listed();

        Assert.True(rows.Count >= 25, $"Only {rows.Count} listed routes were read; the reader is not seeing the screen's list.");
        Assert.Contains(rows, row => row is ("imports", "POST", "/api/integrations/processors/events"));
        Assert.Contains(rows, row => row is ("write", "DELETE", "/api/notification-webhooks/{id}"));
    }

    [Fact]
    public async Task Every_listed_route_is_a_real_route_that_lets_in_a_key_with_exactly_the_listed_scope_and_refuses_one_without_it()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var mapped = RouteMap.Read(app.Services);
        var problems = new List<string>();

        foreach (var (scope, method, path) in Listed())
        {
            var route = mapped.FirstOrDefault(item => item.Method == method && string.Equals(item.Pattern.Trim('/'), path.Trim('/'), StringComparison.OrdinalIgnoreCase));
            if (route is null)
            {
                if (!OnlyOnHostBuilds.Contains(path))
                {
                    problems.Add($"{method} {path} is on the screen but is not a mapped route");
                }

                continue;
            }

            using var allowed = await SendAsync(app, scope, method, route.Path);
            if (allowed.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            {
                problems.Add($"{method} {path}: the screen says {scope}, but a key holding exactly that is refused ({(int)allowed.StatusCode})");
            }

            // The request gate lets a route's whole family in on any one of its scopes (every /api/integrations route takes imports or
            // queue), so the refused key holds none of those and not the listed scope either.
            var gate = DelunoRequestPipeline.ResolveApiScopes(new PathString(route.Path), method);
            var others = RouteMap.AllScopes.Where(item => item != scope && !gate.Contains(item)).ToArray();
            if (others.Length == 0)
            {
                continue;
            }

            using var refused = await SendAsync(app, string.Join(",", others), method, route.Path);
            if (refused.StatusCode != HttpStatusCode.Forbidden)
            {
                problems.Add($"{method} {path}: the screen says {scope}, but a key holding {string.Join(",", others)} (none of the gate's {string.Join(",", gate)}, nor {scope}) got {(int)refused.StatusCode}");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    private static async Task<HttpResponseMessage> SendAsync(ApplicationTestHost app, string scopes, string method, string path)
    {
        var client = await RouteMap.KeyClientAsync(app, scopes);
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method is "POST" or "PUT") request.Content = JsonContent.Create(new { });
        return await client.SendAsync(request);
    }
}
