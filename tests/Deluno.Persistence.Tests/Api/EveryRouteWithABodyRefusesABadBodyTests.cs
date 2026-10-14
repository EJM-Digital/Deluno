using System.Net;
using System.Reflection;
using System.Text;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1275: every route that takes a JSON body, found from the endpoint map, is sent a missing body, a body over 1 MB (no route
/// sets a smaller limit of its own) and a body whose members all have the wrong type. Each must be answered with a 4xx, never a
/// 5xx and never an unhandled exception. A missing, malformed or wrongly typed body is also answered with a plain message that
/// names the member at fault (#1280). Routes that fail today on size are listed below with what they do, each pinned by a skipped
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

    /// <summary>
    /// With no content type the routing step drops these routes, whose body is JSON, and the sibling route `/api/{kind}/{id}/search`
    /// matches "bulk" as an id and answers 404. That is a different route answering, not a binding failure; sent as JSON they answer with a message.
    /// </summary>
    private static readonly IReadOnlySet<string> AnotherRouteAnswersWithoutAContentType = new HashSet<string>(StringComparer.Ordinal)
    {
        "POST /api/movies/bulk/search",
        "POST /api/series/bulk/search"
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
    public async Task Every_route_with_a_JSON_body_refuses_a_wrongly_typed_member_with_a_4xx()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var wrong = new List<string>();
        foreach (var route in BodyRoutes(app))
        {
            var (status, detail) = await SendAsync(app, route, Json(WronglyTyped(route.JsonBodyType!)));
            if (!Is4xx(status))
            {
                wrong.Add($"{route.Label} answered {detail} to {WronglyTyped(route.JsonBodyType!)}");
            }
        }

        AssertNone(wrong, "Routes that did not refuse a wrongly typed body:");
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

    /// <summary>
    /// #1280: a refused body is answered with a plain message, in the <c>message</c> member the web app and the SDK read first.
    /// Returns what is wrong with the answer, or null when it is a 4xx carrying a non-empty message.
    /// </summary>
    internal static string? WhatIsWrongWithTheRefusal(int status, string body, IReadOnlyCollection<string>? membersThatMustBeNamed = null)
    {
        if (!Is4xx(status))
        {
            return $"answered {status}, not a 4xx";
        }

        string? message;
        try
        {
            using var document = JsonDocument.Parse(body);
            message = document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("message", out var found) && found.ValueKind == JsonValueKind.String
                ? found.GetString()
                : null;

            // A handler that checks a member itself (not the framework's binding) answers a validation problem; its
            // reasons in "errors" are plain sentences and are what the web app reads when there is no "message".
            if (string.IsNullOrWhiteSpace(message) && document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
            {
                message = string.Join(" ", errors.EnumerateObject().SelectMany(member => member.Value.EnumerateArray().Select(reason => member.Name + ": " + reason.GetString())));
            }
        }
        catch (JsonException)
        {
            return $"answered {status} with a body that is not JSON ('{Shorten(body)}')";
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            return $"answered {status} with no message ('{Shorten(body)}')";
        }

        if (membersThatMustBeNamed is { Count: > 0 } && !membersThatMustBeNamed.Any(member => message.Contains(member, StringComparison.OrdinalIgnoreCase)))
        {
            return $"answered {status} '{Shorten(message)}', which names none of {string.Join(", ", membersThatMustBeNamed)}";
        }

        return message.Contains("System.", StringComparison.Ordinal) || message.Contains("Path:", StringComparison.Ordinal) || message.Contains("LineNumber", StringComparison.Ordinal)
            ? $"answered {status} '{Shorten(message)}', which carries framework wording and not a plain message"
            : null;
    }

    private static string Shorten(string text) => text.Length <= 160 ? text : text[..160] + "...";

    [Fact]
    public async Task Every_route_with_a_JSON_body_answers_a_missing_body_with_a_plain_message()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var wrong = new List<string>();
        foreach (var route in BodyRoutes(app).Where(route => !route.JsonBodyOptional))
        {
            // No content type at all, and an empty body sent as JSON: two ways to send nothing.
            foreach (var (name, content) in new (string, Func<HttpContent?>)[] { ("no body", () => null), ("an empty JSON body", () => Json("")) })
            {
                var (status, _, body) = await SendWithBodyAsync(app, route, content());
                if (name == "no body" && AnotherRouteAnswersWithoutAContentType.Contains(route.Label))
                {
                    // Sent with no content type, the matcher drops this route and a sibling route with a parameter takes the request.
                    if (status != 404)
                    {
                        wrong.Add($"{route.Label} answered {status} to no body; it is listed as answered by another route (404)");
                    }

                    continue;
                }

                var problem = WhatIsWrongWithTheRefusal(status, body);
                if (problem is not null)
                {
                    wrong.Add($"{route.Label} {problem} for {name}");
                }
            }
        }

        AssertNone(wrong, "Routes that did not explain a missing body:");
    }

    [Fact]
    public async Task Every_route_with_a_JSON_body_answers_malformed_JSON_with_a_plain_message()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var wrong = new List<string>();
        foreach (var route in BodyRoutes(app))
        {
            var (status, _, body) = await SendWithBodyAsync(app, route, Json("{ \"name\": "));
            var problem = WhatIsWrongWithTheRefusal(status, body);
            if (problem is not null)
            {
                wrong.Add($"{route.Label} {problem}");
            }
        }

        AssertNone(wrong, "Routes that did not explain malformed JSON:");
    }

    [Fact]
    public async Task Every_route_with_a_JSON_body_names_the_wrongly_typed_member_in_a_plain_message()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var wrong = new List<string>();
        foreach (var route in BodyRoutes(app))
        {
            var sent = WronglyTyped(route.JsonBodyType!);
            var members = sent.StartsWith('{') ? JsonDocument.Parse(sent).RootElement.EnumerateObject().Select(member => member.Name).ToArray() : [];
            var (status, _, body) = await SendWithBodyAsync(app, route, Json(sent));
            var problem = WhatIsWrongWithTheRefusal(status, body, members);
            if (problem is not null)
            {
                wrong.Add($"{route.Label} {problem} to {sent}");
            }
        }

        AssertNone(wrong, "Routes that did not name the wrong member:");
    }

    /// <summary>
    /// #1280: routes that read their body by hand inside the handler, not through binding, so the framework never sees a failure.
    /// The source scan below finds every such read; each file must be listed here with how many reads it has and the routes they
    /// belong to, and each route is then walked with a malformed body. A new hand-read in any file fails the scan until it is
    /// listed and walked, which is the point.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, (int Reads, string[] Routes)> HandReads = new Dictionary<string, (int, string[])>(StringComparer.Ordinal)
    {
        ["src/Deluno.Api/Network/BundledComponentCredentials.cs"] = (1, ["POST /api/system/bundled-components/{kind}/connect"]),
        ["src/Deluno.Integrations/Seerr/SeerrEndpointRouteBuilderExtensions.cs"] = (1, ["POST /api/seerr-servers/{id}/webhook"])
    };

    private static readonly System.Text.RegularExpressions.Regex HandRead = new(
        @"\b(?:[Rr]equest|HttpContext\.Request)\.(?:Body|BodyReader|Form|ReadFromJsonAsync|ReadFormAsync)\b|\b[Rr]equest\.ReadFromJsonAsync|JsonDocument\.ParseAsync\(\s*(?:[Hh]ttpContext\.)?[Rr]equest\.Body",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    [Fact]
    public void The_source_scan_finds_every_route_that_reads_its_body_by_hand_and_each_is_walked()
    {
        var root = RepositoryRoot();
        var found = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal))
            {
                continue;
            }

            var reads = File.ReadLines(file).Count(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal) && HandRead.IsMatch(line));
            if (reads > 0)
            {
                found[relative] = reads;
            }
        }

        var problems = new List<string>();
        foreach (var (file, reads) in found)
        {
            if (!HandReads.TryGetValue(file, out var listed))
            {
                problems.Add($"{file} reads a request body by hand ({reads}x) and is not walked: list it in HandReads with its routes, and give each a refusal of a malformed body that names what was wrong.");
            }
            else if (listed.Reads != reads)
            {
                problems.Add($"{file} reads a request body by hand {reads}x but HandReads lists {listed.Reads}x: walk the new read.");
            }
        }

        foreach (var file in HandReads.Keys.Except(found.Keys))
        {
            problems.Add($"{file} is listed in HandReads but no longer reads a body by hand; remove it.");
        }

        AssertNone(problems, "Hand-read bodies the walk does not know:");
    }

    [Fact]
    public async Task Every_route_that_reads_its_body_by_hand_answers_a_malformed_body_with_a_plain_message()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var routes = RouteMap.Read(app.Services);
        foreach (var listed in HandReads.Values.SelectMany(entry => entry.Routes))
        {
            Assert.True(routes.Any(route => route.Label == listed), $"{listed} is listed in HandReads but is not a mapped route.");
        }

        var wrong = new List<string>();

        // The Bundled-components connect route: a signed-in caller, a body that is not JSON.
        {
            var route = routes.Single(candidate => candidate.Label == "POST /api/system/bundled-components/{kind}/connect");
            var (status, _, body) = await SendWithBodyAsync(app, route, Json("{ \"username\": "));
            var problem = WhatIsWrongWithTheRefusal(status, body);
            if (problem is not null) wrong.Add($"{route.Label} {problem}");
        }

        // The Seerr webhook: signs in with the secret Deluno issued, then sends a body that is not JSON.
        {
            var created = await app.Client.PostAsJsonAsync("/api/seerr-servers", new { baseUrl = "http://request.example:5055", apiKey = "k", isEnabled = true });
            created.EnsureSuccessStatusCode();
            var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
            var issued = await app.Client.PostAsync($"/api/seerr-servers/{id}/webhook-secret", content: null);
            issued.EnsureSuccessStatusCode();
            var secret = (await issued.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("secret").GetString()!;

            using var anonymous = app.CreateAnonymousClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/seerr-servers/{id}/webhook") { Content = Json("{ \"notification_type\": ") };
            request.Headers.TryAddWithoutValidation("Authorization", secret);
            using var response = await anonymous.SendAsync(request);
            var problem = WhatIsWrongWithTheRefusal((int)response.StatusCode, await response.Content.ReadAsStringAsync());
            if (problem is not null) wrong.Add($"POST /api/seerr-servers/{{id}}/webhook {problem}");
        }

        AssertNone(wrong, "Hand-read routes that did not explain a malformed body:");
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Deluno.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Deluno.slnx not found above the test output.");
    }

    private static IEnumerable<MappedRoute> BodyRoutes(ApplicationTestHost app)
        => RouteMap.Read(app.Services).Where(route => route.JsonBodyType is not null && !route.TakesAFile && !EveryRouteRefusesAMissingOrWrongCredentialTests.Anonymous.ContainsKey(route.Label));

    private static bool Is4xx(int status) => status is >= 400 and < 500;

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<(int Status, string Detail)> SendAsync(ApplicationTestHost app, MappedRoute route, HttpContent? content)
    {
        var (status, detail, _) = await SendWithBodyAsync(app, route, content);
        return (status, detail);
    }

    private static async Task<(int Status, string Detail, string Body)> SendWithBodyAsync(ApplicationTestHost app, MappedRoute route, HttpContent? content)
    {
        using var request = new HttpRequestMessage(new HttpMethod(route.Method), route.Path) { Content = content };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            using var response = await app.Client.SendAsync(request, cts.Token);
            var body = await response.Content.ReadAsStringAsync(cts.Token);
            return ((int)response.StatusCode, ((int)response.StatusCode).ToString(), body);
        }
        catch (OperationCanceledException)
        {
            return (0, "no answer in 30 seconds", "");
        }
        catch (Exception exception)
        {
            // The in-process host hands an unhandled exception to the caller; the real host answers it with a 500.
            return (500, $"500 (unhandled {exception.GetType().Name}: {exception.Message.Split('\n')[0]})", "");
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
