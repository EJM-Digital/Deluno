using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1098: every download records why it was started when it is sent, from facts
/// Deluno already holds, and the dispatch API carries it as startedBy.
/// </summary>
public sealed class DispatchStartedByTests
{
    [Fact]
    public async Task Each_way_a_download_starts_records_its_reason_and_the_api_says_it()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var factory = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        var queue = app.Services.GetRequiredService<IJobQueueRepository>();
        var library = await app.Services.GetRequiredService<ILibrariesRepository>().CreateLibraryAsync(
            new CreateLibraryRequest(
                "Films", "movies", "library", Path.Combine(app.DataRoot, "movies"), Path.Combine(app.DataRoot, "downloads"), null,
                "direct-import", null, null, null, null, true, true, true, 12, 36, 25),
            CancellationToken.None);

        // A film that came in as a Seerr request, one that came from a Trakt list, and a plain one.
        await ExecuteAsync(factory, DelunoDatabaseNames.Platform,
            """
            INSERT INTO seerr_acquisitions (id, connection_id, connection_name, media_type, entity_id, title, seerr_media_id, status, message, created_utc, updated_utc)
            VALUES ('q1', 'c', 'Seerr', 'movies', 'film-seerr', 'Requested', 1, 'added', NULL, '2026-10-01T00:00:00Z', '2026-10-01T00:00:00Z');
            INSERT INTO intake_title_origins (id, source_id, source_name, provider, media_type, entity_id, entry_key, title, first_seen_utc, last_seen_utc)
            VALUES ('o1', 's1', 'My list', 'trakt', 'movies', 'film-list', 'k', 'Listed', '2026-10-01T00:00:00Z', '2026-10-01T00:00:00Z');
            """);

        async Task<JsonElement> SendAsync(string entityId, string? startedBy, string? titleEntityId = null)
        {
            var id = await queue.RecordDownloadDispatchAsync(
                library.Id, "movies", "movie", entityId, $"{entityId}.2020.1080p-X", "Idx", "client-1", "qBittorrent", "sent", null,
                cancellationToken: CancellationToken.None, startedBy: startedBy, titleEntityId: titleEntityId);
            return (await app.Client.GetFromJsonAsync<JsonElement>($"/api/v1/download-dispatches/{id}")).GetProperty("dispatch");
        }

        // A person's grab or search, and an upgrade hunt, are stored as said.
        var manual = await SendAsync("film-a", DownloadStartedBy.Manual);
        Assert.Equal("manual", manual.GetProperty("startedBy").GetString());
        Assert.Equal("You asked", manual.GetProperty("startedByLabel").GetString());
        Assert.Equal("upgrade", (await SendAsync("film-b", DownloadStartedBy.Upgrade)).GetProperty("startedBy").GetString());

        // A missing-title search stays "missing" for a title nothing else brought in.
        var missing = await SendAsync("film-c", DownloadStartedBy.Missing);
        Assert.Equal("missing", missing.GetProperty("startedBy").GetString());
        Assert.Equal("Missing", missing.GetProperty("startedByLabel").GetString());

        // The same search for a title Seerr or a list brought in says where it came from.
        var seerr = await SendAsync("film-seerr", DownloadStartedBy.Missing);
        Assert.Equal("seerr", seerr.GetProperty("startedBy").GetString());
        var list = await SendAsync("film-list", DownloadStartedBy.Missing);
        Assert.Equal("list", list.GetProperty("startedBy").GetString());
        Assert.Equal("Trakt", list.GetProperty("startedByDetail").GetString());
        Assert.Equal("From Trakt", list.GetProperty("startedByLabel").GetString());

        // A title the library already fetched is no longer "from Seerr".
        await ExecuteAsync(factory, DelunoDatabaseNames.Jobs, "UPDATE download_dispatches SET import_status = 'imported' WHERE entity_id = 'film-seerr';");
        Assert.Equal("missing", (await SendAsync("film-seerr", DownloadStartedBy.Missing)).GetProperty("startedBy").GetString());

        // A re-grab keeps the reason of the title's earlier download; with none to keep it has none.
        Assert.Equal("manual", (await SendAsync("film-a", DownloadStartedBy.Inherit)).GetProperty("startedBy").GetString());
        var unknown = await SendAsync("film-never-seen", DownloadStartedBy.Inherit);
        Assert.Equal(JsonValueKind.Null, unknown.GetProperty("startedBy").ValueKind);

        // A caller that says nothing records nothing; it is never guessed.
        var silent = await SendAsync("film-silent", null);
        Assert.Equal(JsonValueKind.Null, silent.GetProperty("startedBy").ValueKind);
        Assert.Equal(JsonValueKind.Null, silent.GetProperty("startedByLabel").ValueKind);
    }

    private static async Task ExecuteAsync(IDelunoDatabaseConnectionFactory factory, string database, string sql)
    {
        await using var connection = await factory.OpenConnectionAsync(database, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
