using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1265: a key granted only the <c>imports</c> scope is the key for a processor's hand-off work. It reads the two routes Weir
/// reads (the hand-offs and the download destinations) and nothing else that <c>read</c> opens. A new GET route is checked here
/// without anyone remembering to add it.
/// </summary>
public sealed class AnImportsKeyReadsOnlyTheHandOffRoutesTests
{
    private static readonly string[] HandOffReads =
    [
        "/api/integrations/processors/handoffs",
        "/api/integrations/processors/download-destinations"
    ];

    [Fact]
    public async Task A_key_with_only_the_imports_scope_reads_the_two_hand_off_routes()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        using var client = await KeyClientAsync(app, "imports");

        foreach (var path in HandOffReads)
        {
            using var response = await client.GetAsync(path);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path} answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }
    }

    [Fact]
    public async Task A_key_with_only_the_imports_scope_is_refused_on_every_other_GET_route()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        using var client = await KeyClientAsync(app, "imports");

        var routes = GetRoutes(app);
        Assert.True(routes.Count > 50, $"Only {routes.Count} GET routes found; the walk is not seeing the endpoint map.");

        var notRefused = new List<string>();
        foreach (var path in routes.Except(HandOffReads, StringComparer.OrdinalIgnoreCase))
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (response.StatusCode != HttpStatusCode.Forbidden && response.StatusCode != HttpStatusCode.Unauthorized)
            {
                notRefused.Add($"{path} answered {(int)response.StatusCode}");
            }
        }

        Assert.True(notRefused.Count == 0, "An imports-only key got through on:" + Environment.NewLine + string.Join(Environment.NewLine, notRefused));
    }

    [Fact]
    public async Task A_key_with_only_the_read_scope_is_still_refused_on_the_hand_off_writes()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        using var client = await KeyClientAsync(app, "read");

        foreach (var path in new[]
                 {
                     "/api/integrations/processors/handoffs/no-such-handoff/import-original",
                     "/api/integrations/processors/handoffs/no-such-handoff/outcome/send-again"
                 })
        {
            using var response = await client.PostAsync(path, content: null);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    /// <summary>Every GET route that asks for a credential, with each route parameter filled by 1, which fits an int constraint as well as an id.</summary>
    private static IReadOnlyList<string> GetRoutes(ApplicationTestHost app)
    {
        var paths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in app.Services.GetRequiredService<IEnumerable<EndpointDataSource>>())
        {
            foreach (var endpoint in source.Endpoints.OfType<RouteEndpoint>())
            {
                var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
                if (methods is null || !methods.Contains("GET", StringComparer.OrdinalIgnoreCase)) continue;
                if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null) continue;
                if (endpoint.Metadata.GetMetadata<IAuthorizeData>() is null) continue;
                paths.Add(Fill(endpoint.RoutePattern));
            }
        }

        return paths.ToArray();
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
                    RoutePatternParameterPart parameter => parameter.IsCatchAll ? "1/1" : "1",
                    _ => ""
                };
            }
        }

        return path.Length == 0 ? "/" : path;
    }

    private static async Task<HttpClient> KeyClientAsync(ApplicationTestHost app, string scopes)
    {
        var created = await app.Client.PostAsJsonAsync("/api/api-keys/", new { name = "Scope " + scopes, scopes });
        created.EnsureSuccessStatusCode();
        var key = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("apiKey").GetString()!;
        var client = app.CreateAnonymousClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);
        return client;
    }
}
