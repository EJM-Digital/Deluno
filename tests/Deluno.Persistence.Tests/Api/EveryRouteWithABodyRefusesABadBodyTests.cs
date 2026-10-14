using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1275: every route that takes a JSON body, found from the endpoint map, is sent a missing body, a body over 1 MB (no route
/// sets a smaller limit of its own) and a body whose members all have the wrong type. Each must be answered with a 4xx, never a
/// 5xx and never an unhandled exception. Routes that fail today are listed below with what they do, each pinned by a skipped
/// test that names them, so the owner can decide; the walk itself stays green for every other route and fails if a listed route
/// starts to behave (the list must then shrink).
/// </summary>
public sealed class EveryRouteWithABodyRefusesABadBodyTests
{
    private const int OverOneMegabyte = 1_100_000;

    /// <summary>
    /// A request that would reach the handler of this route does something on the machine itself (opens a window on the screen of
    /// whoever is signed in), which a test must never do. Missing and wrongly typed bodies are refused before the handler, so
    /// only the oversized case is left out.
    /// </summary>
    private static readonly IReadOnlySet<string> OpensAWindowOnTheMachine = new HashSet<string>(StringComparer.Ordinal)
    {
        "POST /api/filesystem/native-folder-picker"
    };

    /// <summary>
    /// Found defect: these routes accept a body over 1 MB (a 1 MB name is saved, or the unknown 1 MB member is ignored and the
    /// rest is stored). No route sets a body limit of its own and the host's default is 30 MB.
    /// </summary>
    private static readonly IReadOnlySet<string> AcceptsAnOversizedBody = new HashSet<string>(StringComparer.Ordinal)
    {
        "POST /api/backups/",
        "PUT /api/backups/settings",
        "POST /api/blocked-releases",
        "POST /api/custom-formats/",
        "POST /api/custom-formats/dry-run",
        "POST /api/custom-formats/import",
        "POST /api/download-health/ignore-list",
        "PUT /api/download-health/keep-seeding",
        "POST /api/filesystem/import/preview",
        "POST /api/filesystem/path-diagnostics",
        "POST /api/integrations/external/import-preview",
        "POST /api/integrations/external/trigger-refresh",
        "POST /api/intelligent-routing/recommend-release",
        "POST /api/metadata/test",
        "POST /api/migration/preview",
        "POST /api/movies/",
        "POST /api/movies/metadata/jobs",
        "PUT /api/notification-preferences/",
        "POST /api/policy-sets/",
        "PUT /api/quality-model/",
        "PUT /api/recycle-bin/settings",
        "POST /api/release-profiles/",
        "PUT /api/release-profiles/mode",
        "PUT /api/release-profiles/order",
        "POST /api/retention/rules/trial",
        "POST /api/search",
        "PUT /api/security/allowed-hosts",
        "PUT /api/security/local-network-auth",
        "POST /api/series/",
        "POST /api/series/metadata/jobs",
        "PATCH /api/settings/",
        "PUT /api/settings/automation",
        "POST /api/setup/completed",
        "PUT /api/setup/draft",
        "PUT /api/setup/progress",
        "POST /api/sharing-rules/",
        "POST /api/tags/",
        "POST /api/trackers/cloudflare-helper/test",
        "PUT /api/trackers/definitions/update-check",
        "PUT /api/updates/preferences",
        "POST /api/v1/guides/trash/preview",
        "POST /api/v1/guides/trash/sync/preview",
        "PUT /api/v1/guides/trash/update-check/settings"
    };

    /// <summary>Found defect: these routes answer a body over 1 MB with an unhandled exception, which the host reports as a 500.</summary>
    private static readonly IReadOnlySet<string> AnswersAnOversizedBodyWithA500 = new HashSet<string>(StringComparer.Ordinal)
    {
        "POST /api/filesystem/import/execute-batch",
        "POST /api/filesystem/import/scan-folder",
        "POST /api/integrations/external/file-changed"
    };

    /// <summary>Found defect: this route takes any JSON value, and a bare number is answered 200 with a per-item error list instead of a 4xx.</summary>
    private static readonly IReadOnlySet<string> AcceptsAWronglyTypedBody = new HashSet<string>(StringComparer.Ordinal)
    {
        "POST /api/custom-formats/import"
    };

    [Fact]
    public async Task The_walk_finds_the_routes_that_take_a_body()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var routes = RouteMap.Read(app.Services).Where(route => route.JsonBodyType is not null).ToArray();

        Assert.True(routes.Length > 150, $"Only {routes.Length} routes with a JSON body found; the walk is not seeing the endpoint map.");
    }

    [Fact]
    public async Task Every_route_with_a_JSON_body_refuses_a_missing_body_with_a_4xx()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var wrong = new List<string>();
        foreach (var route in BodyRoutes(app).Where(route => !route.JsonBodyOptional))
        {
            var (status, detail) = await SendAsync(app, route, content: null);
            if (!Is4xx(status))
            {
                wrong.Add($"{route.Label} answered {detail} to a missing body");
            }
        }

        AssertNone(wrong, "Routes that did not refuse a missing body:");
    }

    [Fact]
    public async Task Every_route_with_a_JSON_body_refuses_a_wrongly_typed_member_with_a_4xx_except_the_listed_defect()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var wrong = new List<string>();
        foreach (var route in BodyRoutes(app))
        {
            var (status, detail) = await SendAsync(app, route, Json(WronglyTyped(route.JsonBodyType!)));
            var refused = Is4xx(status);
            if (AcceptsAWronglyTypedBody.Contains(route.Label))
            {
                if (refused)
                {
                    wrong.Add($"{route.Label} now refuses a wrongly typed body; take it off the defect list");
                }
            }
            else if (!refused)
            {
                wrong.Add($"{route.Label} answered {detail} to {WronglyTyped(route.JsonBodyType!)}");
            }
        }

        AssertNone(wrong, "Routes that did not refuse a wrongly typed body:");
    }

    [Fact(Skip = "Found defect, owner to decide (#1275): a route that takes any JSON value answers a bare number with 200. See AcceptsAWronglyTypedBody.")]
    public async Task Defect_route_refuses_a_wrongly_typed_body()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var wrong = new List<string>();
        foreach (var route in BodyRoutes(app).Where(route => AcceptsAWronglyTypedBody.Contains(route.Label)))
        {
            var (status, detail) = await SendAsync(app, route, Json(WronglyTyped(route.JsonBodyType!)));
            if (!Is4xx(status))
            {
                wrong.Add($"{route.Label} answered {detail}");
            }
        }

        AssertNone(wrong, "Routes that accept a wrongly typed body:");
    }

    [Fact]
    public async Task Every_route_with_a_JSON_body_refuses_a_body_over_one_megabyte_with_a_4xx_except_the_listed_defects()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var known = AcceptsAnOversizedBody.Concat(AnswersAnOversizedBodyWithA500).ToHashSet(StringComparer.Ordinal);

        var wrong = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var route in BodyRoutes(app).Where(route => !OpensAWindowOnTheMachine.Contains(route.Label)))
        {
            var (status, detail) = await SendAsync(app, route, Json(Oversized(route.JsonBodyType!)));
            var refused = Is4xx(status);
            if (known.Contains(route.Label))
            {
                seen.Add(route.Label);
                if (refused)
                {
                    wrong.Add($"{route.Label} now refuses an oversized body ({detail}); take it off the defect list");
                }
            }
            else if (!refused)
            {
                wrong.Add($"{route.Label} answered {detail} to a body over 1 MB");
            }
        }

        foreach (var missing in known.Except(seen))
        {
            wrong.Add($"{missing} is on the defect list but is not a route with a JSON body; remove it");
        }

        AssertNone(wrong, "Routes that did not behave on a body over 1 MB:");
    }

    [Fact(Skip = "Found defect, owner to decide (#1275): routes accept a body over 1 MB. See AcceptsAnOversizedBody; no route sets a body limit of its own and the host default is 30 MB.")]
    public async Task Defect_routes_refuse_a_body_over_one_megabyte()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var wrong = new List<string>();
        foreach (var route in BodyRoutes(app).Where(route => AcceptsAnOversizedBody.Contains(route.Label)))
        {
            var (status, detail) = await SendAsync(app, route, Json(Oversized(route.JsonBodyType!)));
            if (!Is4xx(status))
            {
                wrong.Add($"{route.Label} answered {detail}");
            }
        }

        AssertNone(wrong, "Routes that accept a body over 1 MB:");
    }

    [Fact(Skip = "Found defect, owner to decide (#1275): routes answer a body over 1 MB with an unhandled exception (a 500). See AnswersAnOversizedBodyWithA500.")]
    public async Task Defect_routes_answer_a_body_over_one_megabyte_with_a_4xx_not_a_500()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var wrong = new List<string>();
        foreach (var route in BodyRoutes(app).Where(route => AnswersAnOversizedBodyWithA500.Contains(route.Label)))
        {
            var (status, detail) = await SendAsync(app, route, Json(Oversized(route.JsonBodyType!)));
            if (!Is4xx(status))
            {
                wrong.Add($"{route.Label} answered {detail}");
            }
        }

        AssertNone(wrong, "Routes that answer an oversized body with a 500:");
    }

    private static IEnumerable<MappedRoute> BodyRoutes(ApplicationTestHost app)
        => RouteMap.Read(app.Services).Where(route => route.JsonBodyType is not null && !route.TakesAFile && !EveryRouteRefusesAMissingOrWrongCredentialTests.Anonymous.ContainsKey(route.Label));

    private static bool Is4xx(int status) => status is >= 400 and < 500;

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<(int Status, string Detail)> SendAsync(ApplicationTestHost app, MappedRoute route, HttpContent? content)
    {
        using var request = new HttpRequestMessage(new HttpMethod(route.Method), route.Path) { Content = content };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            using var response = await app.Client.SendAsync(request, cts.Token);
            return ((int)response.StatusCode, ((int)response.StatusCode).ToString());
        }
        catch (OperationCanceledException)
        {
            return (0, "no answer in 30 seconds");
        }
        catch (Exception exception)
        {
            // The in-process host hands an unhandled exception to the caller; the real host answers it with a 500.
            return (500, $"500 (unhandled {exception.GetType().Name}: {exception.Message.Split('\n')[0]})");
        }
    }

    /// <summary>Every member of the body given the wrong kind of value: text where a number or switch goes, a number where text goes.</summary>
    private static string WronglyTyped(Type body)
    {
        if (body == typeof(JsonElement) || body == typeof(object))
        {
            return "12345";
        }

        if (body != typeof(string) && typeof(System.Collections.IEnumerable).IsAssignableFrom(body))
        {
            return "\"not-a-list\"";
        }

        var members = new Dictionary<string, object>();
        foreach (var property in Properties(body))
        {
            var kind = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            members[JsonNamingPolicy.CamelCase.ConvertName(property.Name)] =
                kind == typeof(string) || kind == typeof(DateTime) || kind == typeof(DateTimeOffset) || kind == typeof(Guid)
                    ? 12345
                    : "not-valid-here";
        }

        return JsonSerializer.Serialize(members);
    }

    /// <summary>A valid-looking body with one string member just over 1 MB long (the first text member, else "name").</summary>
    private static string Oversized(Type body)
    {
        var text = Properties(body).FirstOrDefault(property => property.PropertyType == typeof(string));
        var name = text is null ? "name" : JsonNamingPolicy.CamelCase.ConvertName(text.Name);
        return "{\"" + name + "\":\"" + new string('A', OverOneMegabyte) + "\"}";
    }

    private static IEnumerable<PropertyInfo> Properties(Type body)
        => body.IsAbstract || body.IsInterface || body.IsPrimitive
            ? []
            : body.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(property => property.GetIndexParameters().Length == 0);

    private static void AssertNone(List<string> wrong, string heading)
        => Assert.True(wrong.Count == 0, heading + Environment.NewLine + string.Join(Environment.NewLine, wrong));
}
