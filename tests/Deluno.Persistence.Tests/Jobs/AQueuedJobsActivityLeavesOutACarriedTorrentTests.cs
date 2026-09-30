using System.Text.Json;
using Deluno.Jobs.Data;

namespace Deluno.Persistence.Tests.Jobs;

/// <summary>
/// #1062: a connectivity retry carries the .torrent it already fetched so the
/// tracker is not asked again. The "queued" Activity line keeps the rest of the
/// payload but not the file, or each attempt would copy it into Activity.
/// </summary>
public sealed class AQueuedJobsActivityLeavesOutACarriedTorrentTests
{
    [Fact]
    public void The_carried_torrent_is_left_out_and_everything_else_is_kept()
    {
        var payload = JsonSerializer.Serialize(new { releaseName = "Heat.1995.2160p", torrentFile = new byte[4096], attempt = 2 }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var details = SqliteJobStore.ActivityDetailsOf(payload);

        using var document = JsonDocument.Parse(details!);
        Assert.False(document.RootElement.TryGetProperty("torrentFile", out _));
        Assert.Equal("Heat.1995.2160p", document.RootElement.GetProperty("releaseName").GetString());
        Assert.Equal(2, document.RootElement.GetProperty("attempt").GetInt32());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("""{"releaseName":"Heat.1995.2160p"}""")]
    [InlineData("not json but mentions \"torrentFile\"")]
    public void A_payload_without_a_readable_torrent_is_kept_as_it_is(string? payload)
        => Assert.Equal(payload, SqliteJobStore.ActivityDetailsOf(payload));
}
