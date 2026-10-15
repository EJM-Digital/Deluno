using System.Net;
using System.Reflection;
using System.Text;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1275: every route that takes a JSON body, found from the endpoint map, is sent a missing body, a body over 1 MB (no route
/// is over every route's limit) and a body whose members all have the wrong type. Each must be answered with a 4xx, never a
/// 5xx and never an unhandled exception. A missing, malformed or wrongly typed body is answered with a plain message that
/// names the member at fault (#1280). The body limits and text-length limits are #1279 (RequestBodyLimits).
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
    public async Task Every_route_with_a_JSON_body_refuses_a_body_over_one_megabyte_with_a_413_and_a_plain_message()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var wrong = new List<string>();
        foreach (var route in BodyRoutes(app).Where(route => !OpensAWindowOnTheMachine.Contains(route.Label)))
        {
            using var request = new HttpRequestMessage(new HttpMethod(route.Method), route.Path) { Content = Json(Oversized(route.JsonBodyType!, route.Path)) };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                using var response = await app.Client.SendAsync(request, cts.Token);
                var text = await response.Content.ReadAsStringAsync(cts.Token);
                if ((int)response.StatusCode != 413 || !text.Contains("too large", StringComparison.OrdinalIgnoreCase) || WhatIsWrongWithTheRefusal(413, text) is not null)
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
        Assert.Null(WhatIsWrongWithTheRefusal(400, text, [member]));
        Assert.Equal(member, JsonDocument.Parse(text).RootElement.GetProperty("member").GetString());
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
        ["src/Deluno.Integrations/Seerr/SeerrEndpointRouteBuilderExtensions.cs"] = (1, ["POST /api/seerr-servers/{id}/webhook"]),
        // Reads the plan with the same JSON words GET writes (#1342), so the route catches a malformed body itself.
        ["src/Deluno.Quality/QualityEndpointRouteBuilderExtensions.cs"] = (1, ["POST /api/v1/release-preferences/plans"]),
        // The body-limit middleware (#1279) reads every API body before the route; the 413 walk and the malformed-JSON walk send every body route through it.
        ["src/Deluno.Worker/RequestBodyLimits.cs"] = (2, [])
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

        // A release-preference plan: a signed-in caller, a body that is not JSON.
        {
            var route = routes.Single(candidate => candidate.Label == "POST /api/v1/release-preferences/plans");
            var (status, _, body) = await SendWithBodyAsync(app, route, Json("{ \"planId\": "));
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
    private static string Oversized(Type body, string path)
    {
        var text = Properties(body).FirstOrDefault(property => property.PropertyType == typeof(string));
        var name = text is null ? "name" : JsonNamingPolicy.CamelCase.ConvertName(text.Name);
        return "{\"" + name + "\":\"" + new string('A', (int)Math.Max(OverOneMegabyte, Deluno.Worker.RequestBodyLimits.LimitFor(path) + 1_024)) + "\"}";
    }

    private static IEnumerable<PropertyInfo> Properties(Type body)
        => body.IsAbstract || body.IsInterface || body.IsPrimitive
            ? []
            : body.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(property => property.GetIndexParameters().Length == 0);

    private static void AssertNone(List<string> wrong, string heading)
        => Assert.True(wrong.Count == 0, heading + Environment.NewLine + string.Join(Environment.NewLine, wrong));
}
