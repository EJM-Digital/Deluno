using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Deluno.Integrations.Metadata;
using Deluno.Intake.Data;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;
using Deluno.Worker.Intake;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1301: an import list read over a real loopback port. The StevenLu list is the JSON the real file really holds (recorded from the internet) and its
/// missing-file reply is S3's own; the plain feed is a file of titles (no other app's reply); a bare 500 and a server that never answers carry no app shape.
/// A list that cannot be read is said so and neither adds nor removes a title.
/// </summary>
public sealed class ImportListFeedsOnRealHttpTests
{
    private sealed class Server(WebApplication app, string url) : IAsyncDisposable
    {
        public string Url { get; } = url;
        public ValueTask DisposeAsync() => app.DisposeAsync();
    }

    private static async Task<Server> ServeAsync(Func<HttpContext, Task> answer)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.Run(context => answer(context));
        await app.StartAsync();
        return new Server(app, app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First() + "/list");
    }

    private static Func<HttpContext, Task> Replay(RecordedReply reply) => async context =>
    {
        context.Response.StatusCode = reply.Status;
        context.Response.ContentType = reply.Headers.TryGetValue("content-type", out var type) ? type : "application/json";
        await context.Response.WriteAsync(reply.Body ?? string.Empty, Encoding.UTF8);
    };

    private static async Task<string> CreateListAsync(ApplicationTestHost app, string libraryId, string provider, string url)
    {
        var response = await app.Client.PostAsJsonAsync("/api/intake-sources", new Dictionary<string, object?>
        {
            ["name"] = "A list", ["provider"] = provider, ["feedUrl"] = url, ["mediaType"] = "movies", ["libraryId"] = libraryId,
            ["searchOnAdd"] = false, ["isEnabled"] = true, ["syncIntervalHours"] = 12
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static async Task<IntakeSyncService> ServiceAsync(ApplicationTestHost app)
        => (IntakeSyncService)app.Services.GetRequiredService<IIntakeSyncService>();

    private static async Task<(bool Ok, string Message, int? LatencyMs)> TestAsync(ApplicationTestHost app, string listId)
    {
        var source = await app.Services.GetRequiredService<IIntakeRepository>().GetIntakeSourceAsync(listId, CancellationToken.None);
        return await (await ServiceAsync(app)).TestSourceAsync(source!, CancellationToken.None);
    }

    private static string[] Titles(ApplicationTestHost app, CancellationToken token = default)
        => app.Services.GetRequiredService<IMovieCatalogRepository>().ListAsync(token).GetAwaiter().GetResult().Select(movie => movie.Title).Order().ToArray();

    [Fact]
    public async Task The_real_stevenlu_list_is_read_as_titles_with_imdb_ids_and_a_test_says_how_many_it_answered_with()
    {
        var recorded = RecordedReplies.Stevenlu("popular-movies").Exchanges[0].Reply;
        var expected = recorded.Json().AsArray().Select(item => item!["title"]!.GetValue<string>()).ToArray();
        Assert.NotEmpty(expected);
        await using var feed = await ServeAsync(Replay(recorded));
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var listId = await CreateListAsync(app, libraryId, "stevenlu", feed.Url);

        var tested = await TestAsync(app, listId);
        var preview = await (await app.Client.PostAsync($"/api/intake-sources/{listId}/preview", null)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(tested.Ok, tested.Message);
        Assert.Equal($"Answered with {expected.Length} titles.", tested.Message);
        var shown = preview.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(expected.Length, preview.GetProperty("fetchedCount").GetInt32());
        Assert.Equal(expected.Take(shown.Length), shown.Select(item => item.GetProperty("title").GetString()));
        Assert.All(shown, item => Assert.StartsWith("tt", item.GetProperty("imdbId").GetString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_list_whose_file_is_not_there_or_whose_host_errors_is_said_so_by_the_test_and_a_sync_and_nothing_added_before_is_removed()
    {
        var missing = RecordedReplies.Stevenlu("list-not-found").Exchanges[0].Reply;
        Assert.True(missing.Status >= 400);
        var answer = missing;
        var feed = "<?xml version=\"1.0\"?><rss version=\"2.0\"><channel><title>list</title><item><title>Arrival (2016)</title></item></channel></rss>";
        var serving = "feed";
        await using var server = await ServeAsync(async context =>
        {
            if (serving == "feed") { context.Response.ContentType = "application/rss+xml"; await context.Response.WriteAsync(feed); }
            else if (serving == "missing") await Replay(answer)(context);
            else context.Response.StatusCode = 500;
        });
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => services.AddSingleton<IMetadataProvider>(
            new RecordingMetadataProvider([new MetadataSearchResult("tmdb", "329865", "movies", "Arrival", null, 2016, null, null, null, 7.9, [], ["Drama"], null, null)], answerOnlyTheTitleAsked: true)));
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var listId = await CreateListAsync(app, libraryId, "rss", server.Url);
        var service = await ServiceAsync(app);

        var added = await service.RunAsync(listId, null, true, CancellationToken.None);
        serving = "missing";
        var testedMissing = await TestAsync(app, listId);
        var syncedMissing = await service.RunAsync(listId, null, true, CancellationToken.None);
        serving = "error";
        var testedError = await TestAsync(app, listId);
        var syncedError = await service.RunAsync(listId, null, true, CancellationToken.None);

        Assert.Equal(("success", 1), (added.Status, added.AddedCount));
        Assert.False(testedMissing.Ok);
        Assert.Contains(missing.Status.ToString(), testedMissing.Message, StringComparison.Ordinal);
        Assert.Equal(("error", 0), (syncedMissing.Status, syncedMissing.AddedCount));
        Assert.StartsWith("Provider fetch failed.", syncedMissing.Summary);
        Assert.False(testedError.Ok);
        Assert.Contains("500", testedError.Message, StringComparison.Ordinal);
        Assert.Equal(("error", 0), (syncedError.Status, syncedError.AddedCount));
        Assert.Equal(["Arrival"], Titles(app));
        var stored = (await app.Client.GetFromJsonAsync<JsonElement>("/api/intake-sources")).EnumerateArray().Single();
        Assert.Equal("error", stored.GetProperty("lastSyncStatus").GetString());
    }

    [Fact]
    public async Task A_list_host_that_never_answers_times_out_and_is_said_so_without_adding_or_removing_anything()
    {
        await using var server = await ServeAsync(async context =>
        {
            try { await Task.Delay(Timeout.Infinite, context.RequestAborted); } catch (OperationCanceledException) { } // reason: the host that never answers; the list client's 20 s timeout aborts the request, which ends this
        });
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var listId = await CreateListAsync(app, libraryId, "rss", server.Url);

        var result = await (await ServiceAsync(app)).RunAsync(listId, null, true, CancellationToken.None);

        Assert.Equal(("error", 0), (result.Status, result.AddedCount));
        Assert.StartsWith("Provider fetch failed.", result.Summary);
        Assert.Empty(Titles(app));
    }
    [Theory]
    [InlineData("<!DOCTYPE html><html><head><title>Sign in</title></head><body><p>Please sign in to see this list.</p></body></html>")]
    [InlineData("")]
    [InlineData("   \n ")]
    public async Task A_list_address_that_answers_with_a_web_page_or_nothing_is_said_so_in_one_plain_sentence_naming_the_list(string body)
    {
        await using var server = await ServeAsync(async context => { context.Response.ContentType = "text/html"; await context.Response.WriteAsync(body); });
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var listId = await CreateListAsync(app, libraryId, "rss", server.Url);

        var tested = await TestAsync(app, listId);
        var synced = await (await ServiceAsync(app)).RunAsync(listId, null, true, CancellationToken.None);

        var name = (await app.Client.GetFromJsonAsync<JsonElement>("/api/intake-sources")).EnumerateArray().Single().GetProperty("name").GetString();
        var sentence = name + ": the address did not return a list feed. Check the address opens a feed, not a web page.";
        Assert.False(tested.Ok, tested.Message);
        Assert.Equal(sentence, tested.Message);
        Assert.Equal(("error", 0), (synced.Status, synced.AddedCount));
        Assert.Equal("Provider fetch failed. " + sentence, synced.Summary);
        Assert.DoesNotContain("root level", tested.Message, StringComparison.Ordinal);
        Assert.Empty(Titles(app));
    }
}
