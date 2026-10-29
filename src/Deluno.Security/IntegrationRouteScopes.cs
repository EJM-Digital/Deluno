using Microsoft.AspNetCore.Http;

namespace Deluno.Security;

/// <summary>One /api/integrations route and the single scope a key needs to call it (#1410).</summary>
/// <param name="Featured">Whether the API screen lists the route; the rest are still gated by this table.</param>
/// <param name="Query">An example query string the screen shows after the path; never part of the match.</param>
public sealed record IntegrationRouteScope(string Scope, string Method, string Path, bool Featured = false, string? Query = null);

/// <summary>
/// The one table of what each <c>/api/integrations</c> route asks of an API key. The request gate, the authorization
/// policy and the API screen (System > API) all read it, so what a key can do is what the screen says. Before this the
/// gate let any key holding <c>imports</c> or <c>queue</c> through every one of these routes while the screen listed a
/// single scope per route (#1410). A route that is not in the table is refused to every key until it is added.
/// </summary>
public static class IntegrationRouteScopes
{
    public const string Prefix = "/api/integrations";

    public static readonly IReadOnlyList<IntegrationRouteScope> All =
    [
        new("read", "GET", "/api/integrations/external/manifest", Featured: true),
        new("read", "GET", "/api/integrations/external/health", Featured: true),
        new("read", "GET", "/api/integrations/external/queue", Featured: true),
        new("read", "GET", "/api/integrations/external/activity", Featured: true),
        new("read", "GET", "/api/integrations/outbound-throttle"),
        new("imports", "POST", "/api/integrations/external/import-preview", Featured: true),
        new("imports", "POST", "/api/integrations/external/file-changed"),
        new("imports", "POST", "/api/integrations/external/redownload"),
        new("queue", "POST", "/api/integrations/external/trigger-refresh", Featured: true),
        new("imports", "GET", "/api/integrations/processors/handoffs", Featured: true, Query: "?libraryId={libraryId}"),
        new("imports", "GET", "/api/integrations/processors/download-destinations"),
        new("imports", "POST", "/api/integrations/processors/handoffs/{id}/outcome/send-again"),
        new("imports", "POST", "/api/integrations/processors/handoffs/{id}/retry"),
        new("imports", "POST", "/api/integrations/processors/handoffs/{id}/keep-waiting"),
        new("imports", "POST", "/api/integrations/processors/handoffs/{id}/stop-waiting"),
        new("imports", "POST", "/api/integrations/processors/handoffs/{id}/dismiss"),
        new("imports", "POST", "/api/integrations/processors/handoffs/{id}/import-original"),
        new("imports", "POST", "/api/integrations/processors/handoffs/{id}/search-again"),
        new("read", "GET", "/api/integrations/processors/connections"),
        new("imports", "POST", "/api/integrations/processors/connections"),
        new("imports", "PUT", "/api/integrations/processors/connections/{id}"),
        new("imports", "DELETE", "/api/integrations/processors/connections/{id}"),
        new("imports", "POST", "/api/integrations/processors/connections/{id}/test"),
        new("read", "GET", "/api/integrations/processors/connections/{id}/path-mappings"),
        new("imports", "POST", "/api/integrations/processors/connections/{id}/path-mappings"),
        new("imports", "DELETE", "/api/integrations/processors/connections/{id}/path-mappings/{mappingId}"),
        new("imports", "POST", "/api/integrations/processors/output/delete"),
        new("imports", "POST", "/api/integrations/processors/events", Featured: true)
    ];

    /// <summary>True for a path under /api/integrations.</summary>
    public static bool Covers(PathString path) => path.StartsWithSegments(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The one scope the route needs, or empty when the table does not know the route (no key passes; a signed-in
    /// person is not asked for a scope at all). HEAD and OPTIONS count as the GET of the same route.
    /// </summary>
    public static string[] Required(PathString path, string method)
    {
        var asked = HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) ? HttpMethods.Get : method.ToUpperInvariant();
        var segments = Split(path.Value);
        foreach (var route in All)
        {
            if (route.Method == asked && Matches(Split(route.Path), segments))
            {
                return [route.Scope];
            }
        }

        return [];
    }

    private static string[] Split(string? path)
        => (path ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static bool Matches(string[] pattern, string[] actual)
    {
        if (pattern.Length != actual.Length)
        {
            return false;
        }

        for (var index = 0; index < pattern.Length; index++)
        {
            var isParameter = pattern[index].StartsWith('{') && pattern[index].EndsWith('}');
            if (!isParameter && !string.Equals(pattern[index], actual[index], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}
