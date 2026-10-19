using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>#994, #995 and #996 through the API: tag sets, order and default, patterns.</summary>
public sealed class ReleaseRulesApiTests
{
    private static object Rule(string name, string[] tags, string mustContain = "")
        => new
        {
            name,
            tagNames = tags,
            preferredProtocol = "any",
            usenetDelayMinutes = 0,
            torrentDelayMinutes = 0,
            mustContain,
            mustNotContain = "",
            preferredTerms = Array.Empty<object>()
        };

    [Fact]
    public async Task A_tag_belongs_to_one_rule_only_and_the_default_rule_is_unique()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var first = await app.Client.PostAsJsonAsync("/api/release-profiles", Rule("Family", ["kids", "family"]));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var shared = await app.Client.PostAsJsonAsync("/api/release-profiles", Rule("Other", ["Family", "anime"]));
        Assert.Equal(HttpStatusCode.Conflict, shared.StatusCode);
        Assert.Contains("already belongs to the rule 'Family'", await shared.Content.ReadAsStringAsync());

        var fallback = await app.Client.PostAsJsonAsync("/api/release-profiles", Rule("Default", []));
        Assert.Equal(HttpStatusCode.Created, fallback.StatusCode);
        var secondFallback = await app.Client.PostAsJsonAsync("/api/release-profiles", Rule("Another default", []));
        Assert.Equal(HttpStatusCode.Conflict, secondFallback.StatusCode);
    }

    [Fact]
    public async Task PUT_refuses_a_blank_name_like_create_and_leaves_the_saved_name()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var created = await app.Client.PostAsJsonAsync("/api/release-profiles", Rule("Family", ["kids"]));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var made = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = made.RootElement.GetProperty("id").GetString();

        var blankCreate = await app.Client.PostAsJsonAsync("/api/release-profiles", Rule("  ", ["other"]));
        var blankPut = await app.Client.PutAsJsonAsync($"/api/release-profiles/{id}", new { name = "   " });

        Assert.Equal(HttpStatusCode.BadRequest, blankCreate.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, blankPut.StatusCode);
        Assert.Contains("name", await blankPut.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        using var after = JsonDocument.Parse(await app.Client.GetStringAsync($"/api/release-profiles/{id}"));
        Assert.Equal("Family", after.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public async Task A_pattern_that_cannot_be_read_is_refused_with_a_plain_sentence()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var bad = await app.Client.PostAsJsonAsync("/api/release-profiles", Rule("Bad", ["x"], "/(unclosed/i"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("is not a valid pattern", await bad.Content.ReadAsStringAsync());

        var good = await app.Client.PostAsJsonAsync("/api/release-profiles", Rule("Good", ["x"], @"/\b2160p\b/i, remux"));
        Assert.Equal(HttpStatusCode.Created, good.StatusCode);
    }

    [Fact]
    public async Task The_rules_can_be_reordered_and_how_they_are_used_can_be_chosen()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        using var mode = JsonDocument.Parse(await app.Client.GetStringAsync("/api/release-profiles/mode"));
        Assert.Equal("combine", mode.RootElement.GetProperty("mode").GetString());

        var a = await CreateAsync(app, Rule("A", ["a"]));
        var b = await CreateAsync(app, Rule("B", ["b"]));

        var order = await app.Client.PutAsJsonAsync("/api/release-profiles/order", new { ids = new[] { b, a } });
        Assert.Equal(HttpStatusCode.OK, order.StatusCode);
        using var listed = JsonDocument.Parse(await app.Client.GetStringAsync("/api/release-profiles"));
        Assert.Equal(
            new[] { b, a },
            listed.RootElement.EnumerateArray().Select(item => item.GetProperty("id").GetString()!).ToArray());

        var chosen = await app.Client.PutAsJsonAsync("/api/release-profiles/mode", new { mode = "first-match" });
        Assert.Equal(HttpStatusCode.OK, chosen.StatusCode);
        using var after = JsonDocument.Parse(await app.Client.GetStringAsync("/api/release-profiles/mode"));
        Assert.Equal("first-match", after.RootElement.GetProperty("mode").GetString());
    }

    private static async Task<string> CreateAsync(ApplicationTestHost app, object rule)
    {
        var response = await app.Client.PostAsJsonAsync("/api/release-profiles", rule);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("id").GetString()!;
    }
}
