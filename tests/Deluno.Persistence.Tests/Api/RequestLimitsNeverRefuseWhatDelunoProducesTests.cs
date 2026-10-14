using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Deluno.Filesystem;
using Deluno.Persistence.Tests.Support;
using Deluno.Quality.Guides;
using Deluno.Quality.ReleasePreferences;
using Deluno.Worker;
using Microsoft.AspNetCore.Http;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1326: the request limits (#1279) must never refuse what Deluno itself produces. Three ways it did: an issue id (the issue's
/// details encoded, over 270 characters) was limited as a 200-character name; the shipped guide package (about 2 MB with its
/// pinned source inventory) was over a 1 MB body limit; and the package GET returns was not accepted back by apply.
/// </summary>
public sealed class RequestLimitsNeverRefuseWhatDelunoProducesTests
{
    private static JsonElement Body(string json) => JsonDocument.Parse(json).RootElement;

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    [Fact]
    public void The_longest_issue_id_Deluno_can_issue_is_within_the_limit_for_an_issue_id()
    {
        // Worst case for the JSON writer: every character becomes a six-character escape (a non-ASCII letter, a quote, an angle bracket).
        var path = new string('é', RequestBodyLimits.PathLength);
        var title = new string('<', RequestBodyLimits.DescriptionLength);
        var orphan = FilesystemIssueIds.Encode("orphanFile", "movies", Guid.NewGuid().ToString(), path, null, path);
        var missing = FilesystemIssueIds.Encode("missingTrackedFile", "tv", Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), path, title);
        var windows = FilesystemIssueIds.Encode("missingTrackedFile", "movies", "id", "id", null, new string('\\', RequestBodyLimits.PathLength), title);

        foreach (var id in new[] { orphan, missing, windows })
        {
            Assert.True(id.Length <= RequestBodyLimits.IssueIdLength, $"an id of {id.Length} characters is over the limit {RequestBodyLimits.IssueIdLength}");
            Assert.Null(RequestBodyLimits.FirstTooLong(Body($"{{\"issueId\":\"{id}\"}}")));
        }

        Assert.Equal(RequestBodyLimits.IssueIdLength, RequestBodyLimits.TextLimit("issueId"));
        Assert.True(RequestBodyLimits.IssueIdLength < RequestBodyLimits.DefaultBytes, "an id must fit the default body limit with room for the rest of the request");
    }

    [Fact]
    public void An_id_one_character_over_the_longest_possible_is_still_refused_in_plain_words()
    {
        var message = RequestBodyLimits.FirstTooLong(Body($"{{\"issueId\":\"{new string('A', RequestBodyLimits.IssueIdLength + 1)}\"}}"));

        Assert.NotNull(message);
        Assert.Contains("issue id is too long", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_longest_issue_id_Deluno_can_issue_is_read_by_the_repair_route_not_refused_by_a_limit()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var path = new string('é', RequestBodyLimits.PathLength);
        var id = FilesystemIssueIds.Encode("orphanFile", "movies", Guid.NewGuid().ToString(), path, null, path);

        using var response = await app.Client.PostAsync("/api/filesystem/reconciliation/repair", Json(JsonSerializer.Serialize(new { issueId = id, action = "cleanup-artifact" })));
        var text = await response.Content.ReadAsStringAsync();

        Assert.NotEqual(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.DoesNotContain("too long", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("too large", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_guide_routes_take_the_largest_package_Deluno_ships_with_headroom()
    {
        var shipped = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(
            new GuidePackageUpdateRequest(GuidePackageCatalog.Current, "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"),
            ReleasePreferenceJson.Options));

        Assert.True(shipped > RequestBodyLimits.LongListBytes, "the shipped package (with its source inventory) is the reason these routes have their own limit");
        foreach (var route in new[] { "/api/v1/guides/trash/apply", "/api/v1/guides/trash/preview" })
        {
            Assert.True(RequestBodyLimits.LimitFor(route) >= 3L * shipped, $"{route} must take the shipped package ({shipped} bytes) with headroom");
        }
    }

    [Fact]
    public async Task What_the_package_GET_returns_is_accepted_unchanged_by_apply_and_preview()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var returned = await app.Client.GetStringAsync("/api/v1/guides/trash/package");
        // One JSON setup for both: the package as GET wrote it, only its version raised so apply has something new to make active.
        var package = JsonNode.Parse(returned)!.AsObject();
        package["version"] = package["version"]!.GetValue<int>() + 1;
        var version = package["version"]!.GetValue<int>();
        var body = new JsonObject { ["package"] = package }.ToJsonString();

        using var preview = await app.Client.PostAsync("/api/v1/guides/trash/preview", Json(body));
        Assert.True(preview.IsSuccessStatusCode, await preview.Content.ReadAsStringAsync());
        using var apply = await app.Client.PostAsync("/api/v1/guides/trash/apply", Json(body));
        var applied = await apply.Content.ReadAsStringAsync();

        Assert.True(apply.StatusCode == HttpStatusCode.Created, applied);
        Assert.Contains("\"mappingStatus\":\"reviewed\"", applied, StringComparison.Ordinal);
        var current = await app.Client.GetFromJsonAsync<JsonElement>("/api/v1/guides/trash/package");
        Assert.Equal(version, current.GetProperty("version").GetInt32());
    }

    /// <summary>
    /// Next door: the largest request Deluno's own web app or SDK can make of each route (every text member as long as the
    /// middleware lets that member be, every list as full as a screen lists it) is under the body limit for that route, and no
    /// text member in it is over the limit the middleware gives it. Nothing is sent, so no route does anything.
    /// </summary>
    [Fact]
    public async Task Every_route_takes_the_largest_body_its_own_screens_can_make_without_a_limit_refusing_it()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var refused = new List<string>();
        var checkedRoutes = 0;
        foreach (var route in RouteMap.Read(app.Services).Where(route => route.JsonBodyType is not null && !route.TakesAFile).DistinctBy(route => route.Label))
        {
            var json = Largest(route.JsonBodyType!, 0)?.ToJsonString() ?? "{}";
            var bytes = Encoding.UTF8.GetByteCount(json);
            var limit = RequestBodyLimits.LimitFor(route.Path);
            if (bytes > limit)
            {
                refused.Add($"{route.Label}: the largest body is {bytes} bytes, over the {limit} byte limit");
            }

            if (RequestBodyLimits.FirstTooLong(Body(json), new PathString(route.Path)) is { } message)
            {
                refused.Add($"{route.Label}: {message}");
            }

            checkedRoutes++;
        }

        Assert.True(checkedRoutes > 50, $"the walk found only {checkedRoutes} routes with a body");
        Assert.True(refused.Count == 0, "A limit refuses what Deluno's own screens can send:\n" + string.Join("\n", refused));
    }

    /// <summary>The longest value a field on a screen holds in practice: a long Windows path, a long release name. Longer values have their own tests (issue ids, the guide package).</summary>
    private const int FieldChars = 260;

    private static JsonNode? Largest(Type type, int depth)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (depth > 3) return null;
        if (type == typeof(string)) return JsonValue.Create(new string('a', 40));
        if (type == typeof(Guid)) return JsonValue.Create(Guid.NewGuid());
        if (type == typeof(bool)) return JsonValue.Create(true);
        if (type.IsPrimitive || type == typeof(decimal)) return JsonValue.Create(1);
        if (type.IsEnum) return JsonValue.Create(Enum.GetNames(type).First());
        if (type == typeof(DateTime) || type == typeof(DateTimeOffset)) return JsonValue.Create(DateTimeOffset.UtcNow);
        if (type == typeof(JsonElement) || type == typeof(object) || type == typeof(JsonNode)) return new JsonObject();
        if (type.IsArray || (type.IsGenericType && typeof(System.Collections.IEnumerable).IsAssignableFrom(type) && type.GetGenericArguments().Length == 1))
        {
            var item = type.IsArray ? type.GetElementType()! : type.GetGenericArguments()[0];
            var array = new JsonArray();
            for (var i = 0; i < 25; i++)
            {
                if (Largest(item, depth + 1) is { } element) array.Add(element);
            }

            return array;
        }

        if (type.IsGenericType && typeof(System.Collections.IDictionary).IsAssignableFrom(type)) return new JsonObject();
        if (type.IsAbstract || type.IsInterface) return new JsonObject();
        var obj = new JsonObject();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0))
        {
            var name = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
            JsonNode? node;
            if ((Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType) == typeof(string))
            {
                // A text member as long as the middleware lets that member be; a whole-document member as long as a screen could paste.
                node = JsonValue.Create(new string('a', Math.Min(RequestBodyLimits.TextLimit(name) ?? FieldChars, FieldChars)));
            }
            else
            {
                node = Largest(property.PropertyType, depth + 1);
            }

            if (node is not null) obj[name] = node;
        }

        return obj;
    }
}
