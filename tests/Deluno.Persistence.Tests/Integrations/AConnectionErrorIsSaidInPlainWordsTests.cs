using Deluno.Integrations.DownloadClients;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>Golden path rc.30.9: Transfers showed "Deluge queue failed: Error: No connection could be made because the target machine actively refused it".</summary>
public sealed class AConnectionErrorIsSaidInPlainWordsTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-09T10:00:00Z");

    private static DownloadQueueItem Item(string? error, string status = DownloadQueueStatuses.Downloading)
        => new("hash-1", "client-1", "Deluge on this PC", "torrent", "movies", "Pausetest", "Pausetest.2019.1080p.WEB-DL-GRP",
            "movies", status, 50, 0, 0, 1024, 100, 0, "Deluge", error, Now);

    [Theory]
    [InlineData("Error: No connection could be made because the target machine actively refused it")]
    [InlineData("Connection refused")]
    [InlineData("No such host is known.")]
    public void A_raw_connection_error_becomes_a_plain_sentence_naming_who_is_not_answering(string raw)
    {
        var item = DownloadClientHelpers.NormalizeQueueFailure(Item(raw));

        Assert.Equal("Deluge on this PC isn't answering.", item.ErrorMessage);
        Assert.Equal("Deluge on this PC isn't answering.", item.Failure!.Message);
        Assert.DoesNotContain("actively refused", item.Failure.Summary ?? string.Empty);
    }

    [Fact]
    public void An_error_about_something_else_is_left_as_the_client_said_it()
    {
        var item = DownloadClientHelpers.NormalizeQueueFailure(Item("Error: Unregistered torrent"));

        Assert.Equal("Error: Unregistered torrent", item.ErrorMessage);
    }

    [Fact]
    public void A_tracker_that_refused_the_connection_is_the_tracker_not_answering()
        => Assert.Equal("The tracker isn't answering.", DownloadClientHelpers.PlainConnectionFailure("Error: No connection could be made because the target machine actively refused it", "The tracker"));
}
