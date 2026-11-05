using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>GET /api/integrations/processors/handoffs: filtered by library, capped by take, and only for a key that holds the imports scope.</summary>
public sealed class ProcessorHandoffListTests
{
    private static Task<ProcessorHandoffItem> HandoffAsync(ApplicationTestHost app, string libraryId, string queueItemId)
        => app.Services.GetRequiredService<IProcessorRepository>().EnsureProcessorHandoffAsync(
            new CreateProcessorHandoffRequest(
                libraryId, "movies", "client-1", queueItemId,
                $"Film.{queueItemId}.2020.1080p.WEB-DL-DELUNO",
                Path.Combine(Path.GetTempPath(), "Downloads", queueItemId),
                "Weir"),
            CancellationToken.None);

    private static async Task<HttpClient> KeyClientAsync(ApplicationTestHost app, string scopes)
    {
        var created = await app.Client.PostAsJsonAsync("/api/api-keys/", new { name = "Key " + scopes, scopes });
        var key = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("apiKey").GetString()!;
        var client = app.CreateAnonymousClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);
        return client;
    }

    [Fact]
    public async Task The_list_can_be_filtered_to_one_library_and_capped_by_take()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await HandoffAsync(app, "library-1", "a1");
        await HandoffAsync(app, "library-1", "a2");
        await HandoffAsync(app, "library-2", "b1");

        var all = await app.Client.GetFromJsonAsync<JsonElement>("/api/integrations/processors/handoffs");
        var one = await app.Client.GetFromJsonAsync<JsonElement>("/api/integrations/processors/handoffs?libraryId=library-2");
        var capped = await app.Client.GetFromJsonAsync<JsonElement>("/api/integrations/processors/handoffs?libraryId=library-1&take=1");

        Assert.Equal(3, all.GetArrayLength());
        Assert.Equal("b1", Assert.Single(one.EnumerateArray()).GetProperty("queueItemId").GetString());
        Assert.Equal(1, capped.GetArrayLength());
        Assert.Equal("library-1", capped[0].GetProperty("libraryId").GetString());
    }

    [Fact]
    public async Task A_read_key_is_refused_with_403_and_an_imports_key_is_answered()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await HandoffAsync(app, "library-1", "a1");
        using var read = await KeyClientAsync(app, "read");
        using var imports = await KeyClientAsync(app, "imports");

        Assert.Equal(HttpStatusCode.Forbidden, (await read.GetAsync("/api/integrations/processors/handoffs")).StatusCode);

        var answered = await imports.GetAsync("/api/integrations/processors/handoffs");
        Assert.Equal(HttpStatusCode.OK, answered.StatusCode);
        Assert.Equal(1, (await answered.Content.ReadFromJsonAsync<JsonElement>()).GetArrayLength());
    }
}
