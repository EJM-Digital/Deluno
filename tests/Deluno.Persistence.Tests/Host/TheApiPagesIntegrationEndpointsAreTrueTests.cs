using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Deluno.Persistence.Tests.Support;
using Deluno.Security.Contracts;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Host;

/// <summary>
/// System > API Access lists the integration endpoints with the scope each needs. Every row is read from the page's own source and
/// held to the real route table of the shipped pipeline, and to the answer of a real key that holds only the scope the row names,
/// so the page cannot promise a route that is not there or a scope that would be refused.
/// </summary>
public sealed class TheApiPagesIntegrationEndpointsAreTrueTests
{
    private const string Username = "owner";
    private const string Password = "Deluno-Test-2026!";

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Deluno.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    private static readonly Regex Row = new("""\["(?<scope>[a-z]+)", "(?<method>[A-Z|]+)", "(?<path>/[^"]+)"\]""", RegexOptions.Compiled);

    private static IReadOnlyList<(string Scope, string Method, string Path)> ListedEndpoints()
    {
        var page = File.ReadAllText(Path.Combine(RepositoryRoot(), "apps", "web", "src", "routes", "system-api-page.tsx"));
        var rows = Row.Matches(page)
            .Select(match => (match.Groups["scope"].Value, match.Groups["method"].Value, PathOnly(match.Groups["path"].Value)))
            .ToArray();
        Assert.True(rows.Length >= 20, $"expected the page's list of endpoints, found {rows.Length}");
        return rows;
    }

    /// <summary>"/api/x?take=1 (maximum 500)" is the path "/api/x".</summary>
    private static string PathOnly(string listed) => listed.Split('?', ' ')[0];

    private static string Normalise(string route) => Regex.Replace(route.TrimEnd('/'), @"\{[^}]+\}", "{}");

    [Fact]
    public async Task Every_endpoint_the_page_lists_is_a_route_the_server_really_serves_for_each_method_it_names()
    {
        await using var host = await SharedPipelineHost.StartAsync();
        var served = host.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods ?? ["*"])
                .Select(method => (Method: method, Path: Normalise(endpoint.RoutePattern.RawText ?? string.Empty))))
            .ToHashSet();

        var missing = new List<string>();
        foreach (var (_, method, path) in ListedEndpoints())
        {
            foreach (var one in method.Split('|'))
            {
                var key = Normalise(path);
                // A row for a collection names the verbs of the collection and of one item (PUT and DELETE act on /{id}).
                var isServed = served.Contains((one, key)) || served.Contains(("*", key)) || served.Contains((one, key + "/{}"));
                if (!isServed && path is "/api/openapi/v1.json" or "/api/docs")
                {
                    using var answer = await host.Client.GetAsync(path);
                    isServed = answer.StatusCode == HttpStatusCode.OK;
                }

                if (!isServed)
                {
                    missing.Add($"{one} {path}");
                }
            }
        }

        Assert.True(missing.Count == 0, "the page lists routes the server does not serve: " + string.Join(", ", missing));
    }

    private static async Task<HttpResponseMessage> CallAsync(HttpClient client, string key, string method, string path)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), Regex.Replace(path, @"\{[^}]+\}", "x"));
        request.Headers.Add("X-Api-Key", key);
        if (method != "GET")
        {
            request.Content = JsonContent.Create(new { });
        }

        return await client.SendAsync(request);
    }

    private static async Task<string> KeyWithOnlyAsync(SharedPipelineHost host, string scope)
    {
        var bootstrap = await host.Client.PostAsJsonAsync("/api/auth/bootstrap", new BootstrapUserRequest(Username, "Test Owner", Password));
        Assert.True(bootstrap.IsSuccessStatusCode || bootstrap.StatusCode == HttpStatusCode.Conflict);
        var login = await host.Client.PostAsJsonAsync("/api/auth/login", new LoginRequest(Username, Password));
        login.EnsureSuccessStatusCode();
        var session = (await login.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;
        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/api-keys/") { Content = JsonContent.Create(new { name = "Only " + scope, scopes = scope }) };
        create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session);
        using var created = await host.Client.SendAsync(create);
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("apiKey").GetString()!;
    }

    private static async Task<List<string>> RefusedAsync(IEnumerable<(string Scope, string Method, string Path)> rows)
    {
        await using var host = await SharedPipelineHost.StartAsync();
        var keys = new Dictionary<string, string>();
        var refused = new List<string>();
        foreach (var (scope, method, path) in rows)
        {
            if (!keys.TryGetValue(scope, out var key))
            {
                keys[scope] = key = await KeyWithOnlyAsync(host, scope);
            }

            foreach (var one in method.Split('|'))
            {
                using var answer = await CallAsync(host.Client, key, one, path);
                if (answer.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    refused.Add($"{one} {path} with a key holding only \"{scope}\" answered {(int)answer.StatusCode}");
                }
            }
        }

        return refused;
    }

    [Fact]
    public async Task A_key_holding_only_the_scope_the_page_names_is_let_through_on_every_row_that_names_one_method()
    {
        var refused = await RefusedAsync(ListedEndpoints().Where(row => !row.Method.Contains('|')));

        Assert.True(refused.Count == 0, string.Join("; ", refused));
    }

    [Fact]
    public async Task A_key_without_the_scope_a_route_needs_is_refused_there_so_the_scope_on_the_page_is_not_decoration()
    {
        await using var host = await SharedPipelineHost.StartAsync();
        var key = await KeyWithOnlyAsync(host, "queue");

        // The page says the manifest needs "read"; a key with only "queue" must not read it.
        using var answer = await CallAsync(host.Client, key, "GET", "/api/integrations/external/manifest");

        Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
    }

    [Fact]
    public async Task A_key_holding_only_the_scope_the_page_names_is_let_through_for_every_method_of_a_row_of_several()
    {
        var refused = await RefusedAsync(ListedEndpoints().Where(row => row.Method.Contains('|')));

        Assert.True(refused.Count == 0, string.Join("; ", refused));
    }

    [Fact]
    public void Every_listed_route_names_a_scope_the_sign_in_gate_really_asks_for_on_that_method()
    {
        var wrong = new List<string>();
        foreach (var (scope, method, path) in ListedEndpoints())
        {
            foreach (var one in method.Split('|'))
            {
                var asked = Deluno.Worker.DelunoRequestPipeline.ResolveApiScopes(new Microsoft.AspNetCore.Http.PathString(path), one);
                if (!asked.Contains(scope, StringComparer.OrdinalIgnoreCase))
                {
                    wrong.Add($"{one} {path} is listed with \"{scope}\" but the gate asks for {string.Join(" or ", asked)}");
                }
            }
        }

        Assert.True(wrong.Count == 0, string.Join("; ", wrong));
    }
}
