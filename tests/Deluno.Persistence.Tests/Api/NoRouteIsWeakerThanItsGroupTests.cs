using System.Net;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1314: a route under a scoped group asks for that group's scope. The scope each group declares is read from the endpoint
/// metadata (the policy the route carries), and the policy the platform really applies is probed with a key holding every scope
/// except the declared one. The one documented relaxation is that a GET under a write, queue or imports group asks for read
/// (and the named imports hand-off reads for imports); a system group has no such relaxation, because it holds key lists, backups,
/// update settings and the network allow-list.
/// </summary>
public sealed class NoRouteIsWeakerThanItsGroupTests
{
    [Fact]
    public async Task Every_route_under_a_system_group_refuses_a_key_without_the_system_scope_whatever_the_method()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var routes = RouteMap.Read(app.Services).Where(route => !EveryRouteRefusesAMissingOrWrongCredentialTests.Anonymous.ContainsKey(route.Label)).ToArray();
        var scopesByPolicy = await RouteMap.ScopesByPolicyAsync(app.Services, routes);
        var systemRoutes = routes.Where(route => route.GroupScopes(scopesByPolicy).SequenceEqual(["system"])).ToArray();
        Assert.True(systemRoutes.Length >= 10, $"Only {systemRoutes.Length} system-scoped routes found; the walk is not reading the declared scopes.");

        using var client = await RouteMap.KeyClientAsync(app, string.Join(",", RouteMap.AllScopes.Where(scope => scope != "system")));
        var weaker = new List<string>();
        foreach (var route in systemRoutes)
        {
            using var request = new HttpRequestMessage(new HttpMethod(route.Method), route.Path);
            if (route.Method is not ("GET" or "HEAD" or "OPTIONS"))
            {
                request.Content = route.TakesAFile
                    ? new MultipartFormDataContent { { new ByteArrayContent([1, 2, 3]), "file", "walk.bin" } }
                    : new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
            }

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (response.StatusCode != HttpStatusCode.Forbidden)
            {
                weaker.Add($"{route.Label} is in a system group but a key without system got {(int)response.StatusCode}");
            }
        }

        Assert.True(weaker.Count == 0, "Routes weaker than their group:" + Environment.NewLine + string.Join(Environment.NewLine, weaker));
    }
}
