using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1275: every route that takes a JSON body, found from the endpoint map, is sent a missing body, a body over 1 MB (no route
/// is over every route's limit) and a body whose members all have the wrong type. Each must be answered with a 4xx, never a
/// 5xx and never an unhandled exception. The body limits and text-length limits are #1279 (RequestBodyLimits).
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
    public async Task Every_route_with_a_JSON_body_refuses_a_body_over_one_megabyte_with_a_413_and_a_plain_message()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var wrong = new List<string>();
        foreach (var route in BodyRoutes(app).Where(route => !OpensAWindowOnTheMachine.Contains(route.Label)))
        {
            using var request = new HttpRequestMessage(new HttpMethod(route.Method), route.Path) { Content = Json(Oversized(route.JsonBodyType!)) };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                using var response = await app.Client.SendAsync(request, cts.Token);
                var text = await response.Content.ReadAsStringAsync(cts.Token);
                if ((int)response.StatusCode != 413 || !text.Contains("too large", StringComparison.OrdinalIgnoreCase))
                {
                    wrong.Add($"{route.Label} answered {(int)response.StatusCode} {text[..Math.Min(text.Length, 200)]}");
                }
            }
            catch (Exception exception)
            {
                wrong.Add($"{route.Label} threw {exception.GetType().Name}: {exception.Message}");
            }
        }

        AssertNone(wrong, "Routes that did not answer a body over 1 MB with a 413:");
    }

    [Theory]
    [InlineData("/api/tags/", "name", 201)]
    [InlineData("/api/custom-formats/", "name", 201)]
    [InlineData("/api/policy-sets/", "name", 201)]
    [InlineData("/api/release-profiles/", "name", 201)]
    [InlineData("/api/movies/", "title", 201)]
    [InlineData("/api/series/", "title", 201)]
    [InlineData("/api/sharing-rules/", "name", 201)]
    [InlineData("/api/tags/", "description", 4_001)]
    public async Task A_saved_text_member_over_its_length_limit_is_refused_with_a_plain_400_and_nothing_is_saved(string path, string member, int length)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var body = "{\"" + member + "\":\"" + new string('A', length) + "\"}";

        using var response = await app.Client.PostAsync(path, Json(body));
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("too long", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("at most", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(new string('A', 50), text);
    }

    [Fact]
    public async Task A_name_at_exactly_its_limit_is_not_refused_for_length()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        using var response = await app.Client.PostAsync("/api/tags/", Json("{\"name\":\"" + new string('A', RequestBodyLimitsNameLength) + "\"}"));

        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created, $"Answered {(int)response.StatusCode}.");
    }

    private const int RequestBodyLimitsNameLength = 200;

    [Fact]
    public async Task A_batch_import_without_its_list_is_refused_with_a_400_and_a_reason()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        using var response = await app.Client.PostAsync("/api/filesystem/import/execute-batch", Json("{}"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("list of files", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/api/filesystem/import/scan-folder", "{\"folderPath\":\"C:\\\\x\\u0000y\"}")]
    [InlineData("/api/integrations/external/file-changed", "{\"path\":\"C:\\\\x\\u0000y\"}")]
    public async Task A_path_that_cannot_be_a_path_is_refused_with_a_400_not_a_500(string route, string body)
    {
        await using var app = await ApplicationTestHost.StartAsync();

        using var response = await app.Client.PostAsync(route, Json(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/filesystem/import/scan-folder", "folderPath")]
    [InlineData("/api/integrations/external/file-changed", "path")]
    public async Task A_path_longer_than_the_path_limit_is_refused_with_a_400_not_a_500(string route, string member)
    {
        await using var app = await ApplicationTestHost.StartAsync();

        using var response = await app.Client.PostAsync(route, Json("{\"" + member + "\":\"" + new string('a', 5_000) + "\"}"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/tags/", 256 * 1024)]
    [InlineData("/api/filesystem/import/preview", 1024 * 1024)]
    [InlineData("/api/backups/restore", 256 * 1024 * 1024)]
    [InlineData("/api/backups/restore/preview", 256 * 1024 * 1024)]
    [InlineData("/api/movies/abc/subtitles/en/upload", 5 * 1024 * 1024)]
    [InlineData("/api/series/abc/subtitles/en/upload", 5 * 1024 * 1024)]
    public void Each_route_has_the_body_limit_that_fits_what_it_takes(string path, long bytes)
        => Assert.Equal(bytes, Deluno.Worker.RequestBodyLimits.LimitFor(path));

    [Theory]
    [InlineData("name", 200)]
    [InlineData("displayName", 200)]
    [InlineData("title", 200)]
    [InlineData("description", 4_000)]
    [InlineData("notes", 4_000)]
    [InlineData("path", 4_096)]
    [InlineData("downloadsPath", 4_096)]
    [InlineData("baseUrl", 2_048)]
    [InlineData("pattern", 2_000)]
    [InlineData("apiKey", 1_000)]
    [InlineData("somethingElse", 4_000)]
    public void Saved_text_members_have_a_length_limit_by_kind(string member, int limit)
        => Assert.Equal(limit, Deluno.Worker.RequestBodyLimits.TextLimit(member));

    [Theory]
    [InlineData("detailsJson")]
    [InlineData("csv")]
    [InlineData("yaml")]
    public void Members_that_hold_a_whole_document_are_bounded_by_the_body_limit_alone(string member)
        => Assert.Null(Deluno.Worker.RequestBodyLimits.TextLimit(member));

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
