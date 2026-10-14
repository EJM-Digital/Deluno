using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Deluno.Persistence.Tests.Support;
using Deluno.Security;
using Deluno.Security.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1275: every route in the endpoint map refuses a request with no credential (401), a wrong one (401), and a key that lacks the
/// scope the route needs (403). The routes come from the running host's own endpoint map, so a route added tomorrow is walked
/// without anyone writing a test for it. A route that answers without a credential must be on <see cref="Anonymous"/> with its
/// reason; one that is not on the list fails.
/// </summary>
public sealed class EveryRouteRefusesAMissingOrWrongCredentialTests
{
    /// <summary>Routes that answer with no credential on purpose, each with why. Anything else answering anonymously fails the walk.</summary>
    internal static readonly IReadOnlyDictionary<string, string> Anonymous = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["GET /api/auth/bootstrap-status"] = "The sign-in screen asks whether an owner exists yet, before anyone can sign in.",
        ["POST /api/auth/bootstrap"] = "Creates the first owner; it refuses once an owner exists.",
        ["POST /api/auth/login"] = "Is how a person gets a credential in the first place.",
        ["GET /api/calendar/feed.ics"] = "A calendar app subscribes to this address and cannot sign in; it carries its own token in the address.",
        ["POST /api/seerr-servers/{id}/webhook"] = "Seerr calls this and cannot sign in; it carries the secret Deluno issued it in its own header.",
        ["GET /api/metadata/artwork/{cacheKey}"] = "An image tag cannot send a header; the address is an unguessable cache key of already public artwork.",
        ["GET /api/health/live"] = "A container or service supervisor asks whether the process is up.",
        ["GET /api/health/ready"] = "A container or service supervisor asks whether the process is ready.",
        ["GET /health"] = "A container or service supervisor asks whether the process is up."
    };

    [Fact]
    public async Task The_walk_sees_the_whole_endpoint_map()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var routes = RouteMap.Read(app.Services);

        Assert.True(routes.Count > 400, $"Only {routes.Count} routes found; the walk is not seeing the endpoint map.");
        foreach (var listed in Anonymous.Keys)
        {
            Assert.True(routes.Any(route => route.Label == listed), $"{listed} is on the anonymous list but is not a mapped route; remove the stale entry.");
        }
    }

    [Fact]
    public async Task Every_route_answers_401_with_no_credential_unless_it_is_on_the_anonymous_list()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        using var client = app.CreateAnonymousClient();

        var wrong = new List<string>();
        foreach (var route in RouteMap.Read(app.Services))
        {
            var status = await SendAsync(client, route, _ => { });
            if (!Anonymous.ContainsKey(route.Label) && status != HttpStatusCode.Unauthorized)
            {
                wrong.Add($"{route.Label} answered {(int)status} with no credential; it is not on the anonymous list");
            }
        }

        AssertNone(wrong, "Routes that did not refuse a request with no credential:");
    }

    [Fact]
    public async Task Every_route_answers_401_to_a_wrong_key_a_wrong_session_and_a_session_that_no_longer_counts()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        using var client = app.CreateAnonymousClient();
        var forged = await ForgedSessionsAsync(app);

        var credentials = new (string Name, Action<HttpRequestMessage> Apply)[]
        {
            ("a wrong API key in X-Api-Key", request => request.Headers.Add("X-Api-Key", "deluno_not-a-key-anybody-issued")),
            ("a wrong API key as the bearer", request => request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "deluno_not-a-key-anybody-issued")),
            ("a made-up session token", request => request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-session-token")),
            ("a made-up session token in the address", request => request.RequestUri = new Uri(request.RequestUri + (request.RequestUri!.OriginalString.Contains('?') ? "&" : "?") + "access_token=not-a-session-token", UriKind.Relative)),
            ("a real session whose user is gone", request => request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", forged.UnknownUser)),
            ("a real session signed out since (stale security stamp)", request => request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", forged.StaleStamp)),
            ("an expired session", request => request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", forged.Expired))
        };

        var wrong = new List<string>();
        foreach (var route in RouteMap.Read(app.Services).Where(route => !Anonymous.ContainsKey(route.Label)))
        {
            foreach (var (name, apply) in credentials)
            {
                var status = await SendAsync(client, route, apply);
                if (status != HttpStatusCode.Unauthorized)
                {
                    wrong.Add($"{route.Label} answered {(int)status} to {name}");
                }
            }
        }

        AssertNone(wrong, "Routes that did not refuse a wrong credential:");
    }

    [Fact]
    public async Task Every_route_answers_403_to_a_valid_key_that_lacks_the_scope_it_needs()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var routes = RouteMap.Read(app.Services).Where(route => !Anonymous.ContainsKey(route.Label)).ToArray();
        var scopesByPolicy = await RouteMap.ScopesByPolicyAsync(app.Services, routes);

        var clients = new Dictionary<string, HttpClient>();
        var wrong = new List<string>();
        try
        {
            foreach (var route in routes)
            {
                var needed = route.ScopesNeeded(scopesByPolicy);
                if (needed.Count == 0)
                {
                    wrong.Add($"{route.Label} names no scope: it asks for a signed-in caller only, so no key can lack it. Give it a policy.");
                    continue;
                }

                var held = string.Join(", ", RouteMap.AllScopes.Except(needed, StringComparer.OrdinalIgnoreCase));
                if (held.Length == 0)
                {
                    wrong.Add($"{route.Label} needs {string.Join(" or ", needed)}, so every scope opens it");
                    continue;
                }

                if (!clients.TryGetValue(held, out var client))
                {
                    clients[held] = client = await RouteMap.KeyClientAsync(app, held);
                }

                var status = await SendAsync(client, route, _ => { });
                if (status != HttpStatusCode.Forbidden)
                {
                    wrong.Add($"{route.Label} needs {string.Join(" or ", needed)}; a key holding only [{held}] got {(int)status}");
                }
            }
        }
        finally
        {
            foreach (var client in clients.Values) client.Dispose();
        }

        AssertNone(wrong, "Routes that did not refuse a key without the scope they need:");
    }

    private static async Task<HttpStatusCode> SendAsync(HttpClient client, MappedRoute route, Action<HttpRequestMessage> credential)
    {
        using var request = new HttpRequestMessage(new HttpMethod(route.Method), route.Path);
        if (route.Method is not ("GET" or "HEAD" or "OPTIONS"))
        {
            request.Content = route.TakesAFile
                ? new MultipartFormDataContent { { new ByteArrayContent([1, 2, 3]), "file", "walk.bin" } }
                : new StringContent("{}", Encoding.UTF8, "application/json");
        }

        credential(request);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        return response.StatusCode;
    }

    private static async Task<(string UnknownUser, string StaleStamp, string Expired)> ForgedSessionsAsync(ApplicationTestHost app)
    {
        var protection = app.Services.GetRequiredService<IDataProtectionProvider>();
        var owner = (await app.Services.GetRequiredService<ISecurityRepository>().GetSoleUserAsync(CancellationToken.None))!;
        var now = DateTimeOffset.UtcNow;

        return (
            UserAuthorization.IssueAccessToken(protection, owner with { Id = "no-such-user" }, now, now.AddDays(1)),
            UserAuthorization.IssueAccessToken(protection, owner with { SecurityStamp = "a-stamp-from-before-sign-out" }, now, now.AddDays(1)),
            UserAuthorization.IssueAccessToken(protection, owner, now.AddDays(-8), now.AddDays(-1)));
    }

    private static void AssertNone(List<string> wrong, string heading)
        => Assert.True(wrong.Count == 0, heading + Environment.NewLine + string.Join(Environment.NewLine, wrong));
}
