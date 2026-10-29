using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Deluno.Persistence.Tests.Support;
using Deluno.Security;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1410: every /api/integrations route needs exactly the one scope the API screen lists for it, and the gate and the screen
/// read one table (<see cref="IntegrationRouteScopes"/>). A key holding imports or queue used to get through every one of these
/// routes while the screen listed a single scope per route.
/// </summary>
public sealed class TheIntegrationRoutesAskForExactlyTheScopeTheScreenListsTests
{
    private static string Normalise(string route) => Regex.Replace(route.TrimEnd('/'), @"\{[^}:]+(:[^}]+)?\}", "{}").ToLowerInvariant();

    [Fact]
    public async Task Every_integration_route_is_in_the_table_and_every_row_of_the_table_is_a_route()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var mapped = RouteMap.Read(app.Services)
            .Where(route => route.Pattern.StartsWith("/api/integrations", StringComparison.OrdinalIgnoreCase))
            .Select(route => $"{route.Method} {Normalise(route.Pattern)}")
            .ToHashSet();
        var table = IntegrationRouteScopes.All.Select(route => $"{route.Method} {Normalise(route.Path)}").ToList();

        Assert.Empty(mapped.Except(table));
        Assert.Empty(table.Except(mapped));
        Assert.Equal(table.Count, table.Distinct().Count());
    }

    [Fact]
    public async Task A_key_holding_one_scope_gets_into_each_integration_route_only_when_that_scope_is_the_one_the_table_lists()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var routes = RouteMap.Read(app.Services).Where(route => route.Pattern.StartsWith("/api/integrations", StringComparison.OrdinalIgnoreCase)).ToArray();
        var clients = new Dictionary<string, HttpClient>();
        foreach (var scope in RouteMap.AllScopes)
        {
            clients[scope] = await RouteMap.KeyClientAsync(app, scope);
        }

        var problems = new List<string>();
        foreach (var route in routes)
        {
            var listed = IntegrationRouteScopes.All.Single(row => row.Method == route.Method && Normalise(row.Path) == Normalise(route.Pattern)).Scope;
            foreach (var (scope, client) in clients)
            {
                using var request = new HttpRequestMessage(new HttpMethod(route.Method), route.Path);
                if (route.Method is "POST" or "PUT") request.Content = JsonContent.Create(new { });
                using var answer = await client.SendAsync(request);
                var refused = answer.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized;
                if (scope == listed && refused)
                {
                    problems.Add($"{route.Method} {route.Pattern}: a key holding {scope}, the listed scope, was refused ({(int)answer.StatusCode})");
                }
                else if (scope != listed && !refused)
                {
                    problems.Add($"{route.Method} {route.Pattern}: a key holding only {scope} got {(int)answer.StatusCode}, but the table lists {listed}");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public async Task The_screen_gets_the_featured_rows_of_the_same_table()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var served = await app.Client.GetFromJsonAsync<JsonElement>("/api/api-keys/integration-routes");

        var rows = served.EnumerateArray().Select(row => (row.GetProperty("scope").GetString()!, row.GetProperty("method").GetString()!, row.GetProperty("path").GetString()!)).ToArray();
        Assert.Equal(IntegrationRouteScopes.All.Where(route => route.Featured).Select(route => (route.Scope, route.Method, route.Path)), rows);
        Assert.Contains(rows, row => row is ("imports", "GET", "/api/integrations/processors/handoffs"));
        Assert.Contains(rows, row => row is ("queue", "POST", "/api/integrations/external/trigger-refresh"));
    }
}
