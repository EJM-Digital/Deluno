using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>Low-severity API-key rows (#1329), on the whole application with its own databases.</summary>
public sealed class ApiKeyLowGapTests
{
    private static async Task<JsonElement> MakeAsync(ApplicationTestHost app, string scopes, string name = "Test key")
    {
        var made = await app.Client.PostAsJsonAsync("/api/api-keys", new { name, scopes });
        Assert.Equal(HttpStatusCode.OK, made.StatusCode);
        return await made.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static HttpClient WithHeaders(ApplicationTestHost app, params (string Name, string Value)[] headers)
    {
        var client = app.CreateAnonymousClient();
        foreach (var (name, value) in headers)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation(name, value);
        }

        return client;
    }

    [Fact]
    public async Task A_new_key_comes_back_as_deluno_and_forty_three_url_safe_characters_with_a_fourteen_character_prefix_and_an_item_the_screens_can_read()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var made = await MakeAsync(app, "read");

        var key = made.GetProperty("apiKey").GetString()!;
        Assert.Matches("^deluno_[A-Za-z0-9_-]{43}$", key);
        var item = made.GetProperty("item");
        Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("id").GetString()));
        Assert.Equal("Test key", item.GetProperty("name").GetString());
        Assert.Equal(key[..14] + "...", item.GetProperty("prefix").GetString());
        Assert.Equal("read", item.GetProperty("scopes").GetString());
        Assert.NotEqual(JsonValueKind.Null, item.GetProperty("createdUtc").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("lastUsedUtc").ValueKind);
    }

    [Fact]
    public async Task The_Home_Assistant_template_gives_read_write_and_queue_and_mixed_case_repeated_scopes_are_lower_cased_and_de_duplicated()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var template = await MakeAsync(app, "home-assistant");
        Assert.Equal("read, write, queue", template.GetProperty("item").GetProperty("scopes").GetString());

        var mixed = await MakeAsync(app, "READ, Write,read ,QUEUE,write");
        var scopes = mixed.GetProperty("item").GetProperty("scopes").GetString()!;
        Assert.Equal("read, write, queue", scopes);
        var listed = (await app.Client.GetFromJsonAsync<JsonElement>("/api/api-keys")).EnumerateArray()
            .Single(key => key.GetProperty("id").GetString() == mixed.GetProperty("item").GetProperty("id").GetString());
        Assert.Equal("read, write, queue", listed.GetProperty("scopes").GetString());
        Assert.Equal("read, write, queue", await ApiScenes.TextAsync(app, "platform", $"SELECT scopes FROM api_keys WHERE id = '{mixed.GetProperty("item").GetProperty("id").GetString()}'"));
    }

    [Fact]
    public async Task When_both_headers_are_sent_X_Api_Key_wins_over_the_Bearer_one_and_whitespace_around_a_key_is_trimmed()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var key = (await MakeAsync(app, "read")).GetProperty("apiKey").GetString()!;
        const string wrong = "deluno_not-a-key-this-install-ever-made-000000000000";

        using (var goodHeaderBadBearer = WithHeaders(app, ("X-Api-Key", key), ("Authorization", $"Bearer {wrong}")))
        {
            Assert.Equal(HttpStatusCode.OK, (await goodHeaderBadBearer.GetAsync("/api/stats")).StatusCode);
        }

        using (var badHeaderGoodBearer = WithHeaders(app, ("X-Api-Key", wrong), ("Authorization", $"Bearer {key}")))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await badHeaderGoodBearer.GetAsync("/api/stats")).StatusCode);
        }

        using (var padded = WithHeaders(app, ("X-Api-Key", $"  {key}  ")))
        {
            Assert.Equal(HttpStatusCode.OK, (await padded.GetAsync("/api/stats")).StatusCode);
        }

        using (var paddedBearer = WithHeaders(app, ("Authorization", $"Bearer   {key}  ")))
        {
            Assert.Equal(HttpStatusCode.OK, (await paddedBearer.GetAsync("/api/stats")).StatusCode);
        }
    }
}
