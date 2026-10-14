using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Support;

/// <summary>
/// #1275: the application's endpoint map, read from the running host rather than listed by hand, so a new route is walked by the
/// route-by-route security tests the day it is mapped, without anyone writing a test for it.
/// </summary>
internal sealed record MappedRoute(
    string Method,
    string Pattern,
    string Path,
    IReadOnlyList<string> Policies,
    bool AllowsAnonymous,
    bool ImportsMayRead,
    Type? JsonBodyType,
    bool JsonBodyOptional,
    bool TakesAFile)
{
    public string Label => $"{Method} {Pattern}";

    /// <summary>The scopes the route's group declares, read from the first policy in the endpoint metadata (the group's own policy comes before any the route adds).</summary>
    public IReadOnlyList<string> GroupScopes(IReadOnlyDictionary<string, string[]> scopesByPolicy)
        => Policies.Count == 0 ? [] : scopesByPolicy[Policies[0]];

    /// <summary>The scopes a key needs for this route, as the platform asks for them: any GET asks for read (or imports on the two hand-off reads); every other method asks for what its policy names.</summary>
    public IReadOnlyList<string> ScopesNeeded(IReadOnlyDictionary<string, string[]> scopesByPolicy)
    {
        if (Method is "GET" or "HEAD" or "OPTIONS")
        {
            // #1314: a GET under a system-scoped group asks for system, not read.
            if (GroupScopes(scopesByPolicy).SequenceEqual(["system"])) return ["system"];
            return ImportsMayRead ? ["read", "imports"] : ["read"];
        }

        return Policies.Count == 0 ? [] : scopesByPolicy[Policies[0]];
    }
}

internal static class RouteMap
{
    public static readonly string[] AllScopes = ["read", "write", "queue", "imports", "system"];

    public static IReadOnlyList<MappedRoute> Read(IServiceProvider services)
    {
        var routes = new List<MappedRoute>();
        foreach (var source in services.GetRequiredService<IEnumerable<EndpointDataSource>>())
        {
            foreach (var endpoint in source.Endpoints.OfType<RouteEndpoint>())
            {
                var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
                if (methods is null || methods.Count == 0) continue;

                var policies = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
                    .Select(data => data.Policy)
                    .Where(policy => !string.IsNullOrEmpty(policy))
                    .Select(policy => policy!)
                    .ToArray();
                var accepts = endpoint.Metadata.GetMetadata<IAcceptsMetadata>();
                var json = accepts?.ContentTypes.Any(type => type.Contains("json", StringComparison.OrdinalIgnoreCase)) == true;
                var file = accepts?.ContentTypes.Any(type => type.Contains("multipart", StringComparison.OrdinalIgnoreCase)) == true;

                foreach (var method in methods.Select(method => method.ToUpperInvariant()).Distinct())
                {
                    routes.Add(new MappedRoute(
                        method,
                        endpoint.RoutePattern.RawText ?? "",
                        Fill(endpoint.RoutePattern),
                        policies,
                        endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null,
                        endpoint.Metadata.GetMetadata<DelunoImportsScopeMayReadAttribute>() is not null,
                        json ? accepts!.RequestType : null,
                        accepts?.IsOptional ?? false,
                        file));
                }
            }
        }

        return routes.OrderBy(route => route.Pattern, StringComparer.Ordinal).ThenBy(route => route.Method, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Scopes behind each named policy, read from the registered policy so the test does not carry its own copy.</summary>
    public static async Task<IReadOnlyDictionary<string, string[]>> ScopesByPolicyAsync(IServiceProvider services, IEnumerable<MappedRoute> routes)
    {
        var provider = services.GetRequiredService<IAuthorizationPolicyProvider>();
        var result = new Dictionary<string, string[]>();
        foreach (var name in routes.SelectMany(route => route.Policies).Distinct())
        {
            var policy = await provider.GetPolicyAsync(name);
            result[name] = policy!.Requirements.OfType<DelunoScopeRequirement>().SelectMany(requirement => requirement.Scopes).Distinct().ToArray();
        }

        return result;
    }

    /// <summary>Each route parameter filled with 1, which fits an int constraint as well as an id.</summary>
    public static string Fill(RoutePattern pattern)
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
                    RoutePatternParameterPart parameter => parameter.IsCatchAll ? "1/1" : "1",
                    _ => ""
                };
            }
        }

        return path.Length == 0 ? "/" : path;
    }

    /// <summary>A client that presents a key holding exactly these scopes.</summary>
    public static async Task<HttpClient> KeyClientAsync(ApplicationTestHost app, string scopes)
    {
        var created = await app.Client.PostAsJsonAsync("/api/api-keys/", new { name = "Walk " + scopes, scopes });
        created.EnsureSuccessStatusCode();
        var key = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("apiKey").GetString()!;
        var client = app.CreateAnonymousClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);
        return client;
    }
}
