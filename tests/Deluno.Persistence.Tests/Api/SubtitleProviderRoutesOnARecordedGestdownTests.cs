using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Integrations.Subtitles;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The subtitle provider routes (coverage register, area api) against what Gestdown, Addic7ed's public subtitle API, really answered
/// (tests/fixtures/recorded/gestdown: its show search and the English subtitles of Breaking Bad's first episode, and a title it does not
/// hold). Gestdown needs no account, so the whole round is real: a test of the provider before it is saved, the save, the health the test
/// leaves behind, and forgetting it. The provider's HTTP client is answered from the recordings; the route, the provider and the database run.
/// </summary>
public sealed class SubtitleProviderRoutesOnARecordedGestdownTests
{
    private sealed class Replay(string state) : HttpMessageHandler
    {
        private readonly HttpMessageHandler _recorded = RecordedReplies.Handler("gestdown", [RecordedReplies.Gestdown(state)]);

        public List<string> Asked { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked.Add(request.RequestUri!.PathAndQuery);
            var invoker = new HttpMessageInvoker(_recorded, disposeHandler: false);
            return await invoker.SendAsync(request, cancellationToken);
        }
    }

    private static Task<ApplicationTestHost> StartAsync(Replay replay)
        => ApplicationTestHost.StartAsync(replaceServices: services =>
            services.AddHttpClient(SubtitleProviderHttp.ClientName).ConfigurePrimaryHttpMessageHandler(() => replay));

    private static async Task<JsonElement> GestdownAsync(ApplicationTestHost app)
        => (await app.Client.GetFromJsonAsync<JsonElement>("/api/subtitle-providers")).EnumerateArray().Single(item => item.GetProperty("key").GetString() == "gestdown");

    [Fact]
    public async Task Testing_the_provider_before_it_is_saved_runs_a_real_search_and_says_how_many_subtitles_came_back_and_saves_nothing()
    {
        var replay = new Replay("breaking-bad-s01e01-english");
        await using var app = await StartAsync(replay);
        Assert.Equal(JsonValueKind.Null, (await GestdownAsync(app)).GetProperty("configured").ValueKind);

        var response = await app.Client.PostAsJsonAsync("/api/subtitle-providers/gestdown/test", new { providerKey = "gestdown", isEnabled = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.Equal("healthy", result.GetProperty("status").GetString());
        Assert.Equal(6, result.GetProperty("resultCount").GetInt32());
        Assert.Equal("Gestdown answered with 6 subtitle(s) for the test title.", result.GetProperty("message").GetString());
        Assert.Equal(["/shows/search/Breaking%20Bad", "/subtitles/get/31ffb6ce-c000-4079-8912-b3f72057baed/1/1/en"], replay.Asked);
        Assert.Equal(JsonValueKind.Null, (await GestdownAsync(app)).GetProperty("configured").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsJsonAsync("/api/subtitle-providers/no-such-provider/test", new { isEnabled = true })).StatusCode);
    }

    [Fact]
    public async Task A_provider_is_saved_tested_so_its_health_is_kept_and_forgotten_and_forgetting_twice_is_404()
    {
        await using var app = await StartAsync(new Replay("breaking-bad-s01e01-english"));

        var saved = await app.Client.PutAsJsonAsync("/api/subtitle-providers/gestdown", new { providerKey = "gestdown", priority = 5, isEnabled = true });

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var configured = (await GestdownAsync(app)).GetProperty("configured");
        Assert.True(configured.GetProperty("isEnabled").GetBoolean());
        Assert.Equal(5, configured.GetProperty("priority").GetInt32());
        Assert.Equal("untested", configured.GetProperty("healthStatus").GetString());

        var tested = await app.Client.PostAsJsonAsync("/api/subtitle-providers/gestdown/test", new { providerKey = "gestdown", isEnabled = true });
        Assert.True((await tested.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ok").GetBoolean());
        var afterTest = (await GestdownAsync(app)).GetProperty("configured");
        Assert.Equal("healthy", afterTest.GetProperty("healthStatus").GetString());
        Assert.Contains("6 subtitle(s)", afterTest.GetProperty("lastHealthMessage").GetString());
        Assert.Equal(1, await ApiScenes.ScalarAsync(app, "platform", "SELECT COUNT(*) FROM subtitle_providers WHERE provider_key = 'gestdown'"));

        var forgotten = await app.Client.DeleteAsync("/api/subtitle-providers/gestdown");

        Assert.Equal(HttpStatusCode.NoContent, forgotten.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await GestdownAsync(app)).GetProperty("configured").ValueKind);
        Assert.Equal(0, await ApiScenes.ScalarAsync(app, "platform", "SELECT COUNT(*) FROM subtitle_providers WHERE provider_key = 'gestdown'"));
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync("/api/subtitle-providers/gestdown")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PutAsJsonAsync("/api/subtitle-providers/no-such-provider", new { isEnabled = true })).StatusCode);
    }

    [Fact]
    public async Task A_provider_that_answers_but_holds_nothing_for_the_test_title_is_reported_as_degraded_in_plain_words()
    {
        await using var app = await StartAsync(new Replay("show-not-found"));
        await app.Client.PutAsJsonAsync("/api/subtitle-providers/gestdown", new { providerKey = "gestdown", priority = 5, isEnabled = true });

        var response = await app.Client.PostAsJsonAsync("/api/subtitle-providers/gestdown/test", new { providerKey = "gestdown", isEnabled = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.NotEqual("healthy", result.GetProperty("status").GetString());
        var stored = (await GestdownAsync(app)).GetProperty("configured");
        Assert.NotEqual("healthy", stored.GetProperty("healthStatus").GetString());
        Assert.False(string.IsNullOrWhiteSpace(stored.GetProperty("lastHealthMessage").GetString()));
    }
}
