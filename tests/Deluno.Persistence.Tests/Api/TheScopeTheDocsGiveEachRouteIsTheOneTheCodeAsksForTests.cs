using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Deluno.Persistence.Tests.Support;
using Deluno.Worker.ApiParity;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1333: <c>docs/external-integration-api.md</c> names the scope each route needs. This test reads those words, finds the route
/// in the running application's endpoint map, and checks what the sign-in gate and the route's own policy really apply: a key with
/// exactly the documented scopes gets in, and a key missing any one of them is refused. The docs and the code cannot drift again
/// without this failing, whichever side moves.
/// </summary>
public sealed partial class TheScopeTheDocsGiveEachRouteIsTheOneTheCodeAsksForTests
{
    // `GET /api/history` (`read` scope)   |   `GET /api/x` (`read` and `imports` scopes)
    [GeneratedRegex(@"`(?<method>GET|POST|PUT|DELETE|PATCH) (?<path>/api/[^`?\s]+)[^`]*`\s*\((?<scopes>`\w+`(?:(?:,| and)\s*`\w+`)*) scopes?\)", RegexOptions.Multiline)]
    private static partial Regex DocumentedRoute();

    // | `Backup` | Makes a backup now | `system` |   and   | `CheckForUpdate` (`ApplicationUpdate`) | ... | `system` |
    [GeneratedRegex(@"^\|\s*`(?<name>\w+)`[^|]*\|[^|]*\|\s*`(?<scope>\w+)`\s*\|\s*$", RegexOptions.Multiline)]
    private static partial Regex DocumentedCommand();

    private static string Docs() => File.ReadAllText(Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "../../../../../docs/external-integration-api.md")));

    private static IReadOnlyList<(string Method, string Pattern, string[] Scopes)> DocumentedRoutes()
        => DocumentedRoute().Matches(Docs())
            .Select(match => (
                match.Groups["method"].Value,
                match.Groups["path"].Value,
                Regex.Matches(match.Groups["scopes"].Value, @"`(\w+)`").Select(scope => scope.Groups[1].Value).ToArray()))
            .Distinct()
            .ToArray();

    [Fact]
    public void The_document_names_routes_and_commands_to_check()
    {
        var routes = DocumentedRoutes();
        Assert.True(routes.Count >= 6, $"Only {routes.Count} documented routes with a scope were read; the reader is not seeing the document.");
        Assert.Contains(routes, route => route.Method == "DELETE" && route.Pattern == "/api/queue/{clientId}/{queueItemId}" && route.Scopes.SequenceEqual(["queue"]));
        Assert.Contains(routes, route => route.Method == "GET" && route.Pattern == "/api/filesystem/directories" && route.Scopes.Contains("imports"));
        Assert.True(DocumentedCommand().Matches(Docs()).Count >= 8);
    }

    [Fact]
    public async Task Every_documented_route_lets_in_a_key_with_exactly_the_documented_scopes_and_refuses_one_missing_any_of_them()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var mapped = RouteMap.Read(app.Services);
        var problems = new List<string>();

        foreach (var (method, pattern, scopes) in DocumentedRoutes())
        {
            var route = mapped.FirstOrDefault(item => item.Method == method && string.Equals(item.Pattern.TrimStart('/'), pattern.TrimStart('/'), StringComparison.OrdinalIgnoreCase));
            if (route is null)
            {
                problems.Add($"{method} {pattern} is in the document but is not a mapped route");
                continue;
            }

            using var allowed = await SendAsync(app, string.Join(",", scopes), method, route.Path);
            if (allowed.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            {
                problems.Add($"{method} {pattern}: the document gives it {string.Join(" + ", scopes)}, but a key holding exactly that is refused ({(int)allowed.StatusCode})");
            }

            foreach (var missing in scopes)
            {
                var others = RouteMap.AllScopes.Where(scope => scope != missing).ToArray();
                using var refused = await SendAsync(app, string.Join(",", others), method, route.Path);
                if (refused.StatusCode != HttpStatusCode.Forbidden)
                {
                    problems.Add($"{method} {pattern}: the document needs {missing}, but a key holding {string.Join(",", others)} but not {missing} got {(int)refused.StatusCode}");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Every_command_in_the_document_has_the_scope_the_code_asks_for_and_no_command_is_missing_from_it()
    {
        var documented = DocumentedCommand().Matches(Docs())
            .ToDictionary(match => match.Groups["name"].Value, match => match.Groups["scope"].Value, StringComparer.OrdinalIgnoreCase);

        foreach (var (name, scope, _) in CommandNames.All)
        {
            Assert.True(documented.TryGetValue(name, out var given), $"{name} is a command but is not in the document's table");
            Assert.Equal(scope, given);
        }

        Assert.Equal(CommandNames.All.Count, documented.Count);
    }

    [Fact]
    public async Task A_key_with_only_the_scope_of_a_command_reaches_the_command_and_a_key_without_it_is_refused_for_every_command()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        foreach (var (name, scope, _) in CommandNames.All)
        {
            // A name Deluno does not run: the gate and policy let a key with the scope through (400 says the command is unknown), so nothing runs.
            using var reaches = await SendAsync(app, scope, "POST", "/api/command", new { name = "NoSuchCommand" });
            Assert.True(reaches.StatusCode == HttpStatusCode.BadRequest, $"{name}: a key with only {scope} got {(int)reaches.StatusCode} at the command route");

            // The command's own check runs before it does anything, so a key without its scope runs nothing.
            using var refused = await SendAsync(app, string.Join(",", RouteMap.AllScopes.Where(item => item != scope)), "POST", "/api/command", new { name });
            Assert.True(refused.StatusCode == HttpStatusCode.Forbidden, $"{name}: a key without {scope} got {(int)refused.StatusCode}");
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(ApplicationTestHost app, string scopes, string method, string path, object? body = null)
    {
        var client = await RouteMap.KeyClientAsync(app, scopes);
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await client.SendAsync(request);
    }
}
