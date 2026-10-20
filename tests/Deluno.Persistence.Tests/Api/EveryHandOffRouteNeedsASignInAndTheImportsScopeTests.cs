using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Every route under <c>/api/integrations/processors</c> (the hand-off recovery actions, the connections, the output delete,
/// the report route and the download destinations) answers 401 to a caller with no sign-in and 403 to a key without the
/// <c>imports</c> scope (apart from the two settings reads the <c>read</c> scope opens). The routes are read from the live endpoint map, so a route added later is held to it without
/// anyone remembering to list it.
/// </summary>
public sealed class EveryHandOffRouteNeedsASignInAndTheImportsScopeTests
{
    private const string Prefix = "/api/integrations/processors";

    private static IReadOnlyList<(string Method, string Path)> ProcessorRoutes(ApplicationTestHost app)
    {
        var routes = new SortedSet<(string Method, string Path)>();
        foreach (var source in app.Services.GetRequiredService<IEnumerable<EndpointDataSource>>())
        {
            foreach (var endpoint in source.Endpoints.OfType<RouteEndpoint>())
            {
                var path = Fill(endpoint.RoutePattern);
                if (!path.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var method in endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
                {
                    routes.Add((method.ToUpperInvariant(), path));
                }
            }
        }

        return routes.ToArray();
    }

    private static string Fill(RoutePattern pattern)
    {
        var path = "";
        foreach (var segment in pattern.PathSegments)
        {
            path += "/";
            foreach (var part in segment.Parts)
            {
                path += part switch
                {
                    RoutePatternLiteralPart literal => literal.Content,
                    RoutePatternParameterPart => "no-such-id",
                    _ => ""
                };
            }
        }

        return path;
    }

    private static HttpRequestMessage Request((string Method, string Path) route)
        => new(new HttpMethod(route.Method), route.Path)
        {
            Content = route.Method is "POST" or "PUT" or "PATCH" ? JsonContent.Create(new { }) : null
        };

    [Fact]
    public async Task Every_hand_off_route_answers_401_with_no_sign_in_and_403_to_a_key_without_the_imports_scope()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var routes = ProcessorRoutes(app);
        Assert.True(routes.Count >= 18, $"Only {routes.Count} processor routes were found; the walk is not seeing the endpoint map.");
        foreach (var expected in new[]
                 {
                     ("GET", Prefix + "/handoffs"), ("GET", Prefix + "/download-destinations"),
                     ("POST", Prefix + "/events"), ("POST", Prefix + "/output/delete"),
                     ("POST", Prefix + "/handoffs/no-such-id/retry"), ("POST", Prefix + "/handoffs/no-such-id/keep-waiting"),
                     ("POST", Prefix + "/handoffs/no-such-id/stop-waiting"), ("POST", Prefix + "/handoffs/no-such-id/dismiss"),
                     ("POST", Prefix + "/handoffs/no-such-id/import-original"), ("POST", Prefix + "/handoffs/no-such-id/search-again"),
                     ("POST", Prefix + "/handoffs/no-such-id/outcome/send-again")
                 })
        {
            Assert.Contains(expected, routes);
        }

        var created = await app.Client.PostAsJsonAsync("/api/api-keys/", new { name = "Read only", scopes = "read" });
        var key = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("apiKey").GetString()!;
        using var anonymous = app.CreateAnonymousClient();
        using var readOnly = app.CreateAnonymousClient();
        readOnly.DefaultRequestHeaders.Add("X-Api-Key", key);

        var wrong = new List<string>();
        foreach (var route in routes)
        {
            using (var signedOut = Request(route))
            using (var response = await anonymous.SendAsync(signedOut))
            {
                if (response.StatusCode != HttpStatusCode.Unauthorized)
                {
                    wrong.Add($"{route.Method} {route.Path} signed out answered {(int)response.StatusCode}");
                }
            }

            // The connections list and a connection's path mappings are ordinary settings reads: the read scope opens them, as
            // it opens every GET that is not a hand-off read, so a read-only key is told 200 there and 403 everywhere else.
            var settingsRead = route.Method == "GET"
                && (route.Path.EndsWith("/connections", StringComparison.Ordinal) || route.Path.EndsWith("/path-mappings", StringComparison.Ordinal));
            using (var noScope = Request(route))
            using (var response = await readOnly.SendAsync(noScope))
            {
                if (response.StatusCode != (settingsRead ? HttpStatusCode.OK : HttpStatusCode.Forbidden))
                {
                    wrong.Add($"{route.Method} {route.Path} with a read-only key answered {(int)response.StatusCode}");
                }
            }
        }

        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
    }

    [Fact]
    public async Task The_download_destinations_route_says_404_for_a_library_it_does_not_know_and_lists_the_library_it_does()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName;
        var downloads = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Completed")).FullName;
        var made = await app.Client.PostAsJsonAsync("/api/libraries", new { name = "Films", mediaType = "movies", rootPath = root, downloadsPath = downloads });
        made.EnsureSuccessStatusCode();
        var libraryId = (await made.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var unknown = await app.Client.GetAsync(Prefix + "/download-destinations?libraryId=no-such-library");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        var error = await unknown.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("library_not_found", error.GetProperty("error").GetString());

        var known = await app.Client.GetAsync(Prefix + "/download-destinations?libraryId=" + libraryId);
        Assert.Equal(HttpStatusCode.OK, known.StatusCode);
        var libraries = (await known.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("libraries");
        var only = Assert.Single(libraries.EnumerateArray());
        Assert.Equal(libraryId, only.GetProperty("libraryId").GetString());
        Assert.Equal(downloads, only.GetProperty("downloadsPath").GetString());
    }
}
