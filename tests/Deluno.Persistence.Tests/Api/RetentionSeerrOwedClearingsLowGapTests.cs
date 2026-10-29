using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;
using Deluno.Retention;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// A title a retention rule removed whose Seerr record could not be cleared is owed a clearing (#1329):
/// what happens to that debt when the server is gone, switched off, or when no server is switched on.
/// </summary>
public sealed class RetentionSeerrOwedClearingsLowGapTests
{
    private static string Owed(string serverId, string entityId)
        => $"INSERT INTO retention_seerr_cleanups (server_id, entity_id, media_type, title, tmdb_id, tvdb_id, attempts, problem, created_utc, attempted_utc) VALUES ('{serverId}', '{entityId}', 'movies', 'Sintel', '45745', NULL, 2, 'Seerr could not be reached.', '2026-10-01T00:00:00.0000000+00:00', '2026-10-02T00:00:00.0000000+00:00');";

    [Fact]
    public async Task An_owed_clearing_for_a_server_removed_from_Deluno_is_dropped_one_for_a_switched_off_server_waits_and_no_switched_on_server_means_no_sentence()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var saved = await app.Client.PostAsJsonAsync("/api/seerr-servers", new { name = "Switched off", baseUrl = "http://seerr.example:5055", apiKey = "0123456789abcdef0123456789abcdef", isEnabled = false });
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
        var offId = (await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        await ApiScenes.ExecuteAsync(app, "platform", Owed("server-that-was-removed", "film-1"));
        await ApiScenes.ExecuteAsync(app, "platform", Owed(offId, "film-2"));
        var seerr = app.Services.GetRequiredService<IRetentionSeerr>();

        await seerr.RetryAsync(CancellationToken.None);
        var sentence = await seerr.ClearAsync("film-3", "movies", "Sintel", "45745", null, CancellationToken.None);

        var waiting = Assert.Single(await seerr.ListCleanupsAsync(CancellationToken.None));
        Assert.Equal((offId, "film-2", 2), (waiting.ServerId, waiting.EntityId, waiting.Attempts));
        Assert.Null(sentence);
        Assert.Equal(1, await ApiScenes.ScalarAsync(app, "platform", "SELECT COUNT(*) FROM retention_seerr_cleanups"));
    }
}
