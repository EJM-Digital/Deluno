using System.Text.Json;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Jobs;

/// <summary>
/// #1470: the client's refusals of a follow-up call are written into the dispatch's notes when it is recorded. Notes that are
/// already an object keep their other keys, notes that are not an object start again as one, and refusals that are not JSON
/// leave the notes exactly as they were.
/// </summary>
public sealed class AGrabsRefusalsAreKeptInTheNotesWhateverTheNotesWereTests
{
    private const string Refusals = """[{"call":"setLocation","what":"the save folder","reason":"Cannot make save path"}]""";

    private static async Task<JsonElement?> NotesAsync(string? notes, string? refusals)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var id = await app.Services.GetRequiredService<IJobQueueRepository>().RecordDownloadDispatchAsync(
            "lib-1", "movies", "movie", "movie-1", "Film.2024.1080p", "Indexer", "qbittorrent", "qBittorrent", "queued", notes,
            cancellationToken: CancellationToken.None, followUpRefusalsJson: refusals);
        var stored = (await app.Services.GetRequiredService<IDownloadDispatchesRepository>().GetDispatchAsync(id, CancellationToken.None))!.NotesJson;
        return string.IsNullOrWhiteSpace(stored) ? null : JsonDocument.Parse(stored).RootElement.Clone();
    }

    [Fact]
    public async Task Notes_that_are_an_object_keep_their_keys_beside_the_refusals()
    {
        var notes = await NotesAsync("""{"reason":"best match"}""", Refusals);

        Assert.Equal("best match", notes!.Value.GetProperty("reason").GetString());
        Assert.Equal("setLocation", notes.Value.GetProperty("followUpRefusals")[0].GetProperty("call").GetString());
    }

    [Fact]
    public async Task No_notes_become_an_object_holding_only_the_refusals()
    {
        var notes = await NotesAsync(null, Refusals);

        Assert.Equal("setLocation", notes!.Value.GetProperty("followUpRefusals")[0].GetProperty("call").GetString());
    }

    [Fact]
    public async Task Notes_that_are_not_an_object_start_again_as_one()
    {
        var notes = await NotesAsync("[1,2]", Refusals);

        Assert.Equal(JsonValueKind.Object, notes!.Value.ValueKind);
        Assert.True(notes.Value.TryGetProperty("followUpRefusals", out _));
    }

    [Fact]
    public async Task Refusals_that_are_not_JSON_leave_the_notes_as_they_were()
    {
        var notes = await NotesAsync("""{"reason":"best match"}""", "not json");

        Assert.False(notes!.Value.TryGetProperty("followUpRefusals", out _));
        Assert.Equal("best match", notes.Value.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_grab_with_no_refusals_leaves_no_followUpRefusals_key()
    {
        var notes = await NotesAsync("""{"reason":"best match"}""", " ");

        Assert.False(notes!.Value.TryGetProperty("followUpRefusals", out _));
    }
}
