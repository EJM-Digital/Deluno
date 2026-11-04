using Deluno.Contracts;
using Deluno.Media;
using Deluno.Worker.Attention;

namespace Deluno.Worker.Tests.Attention;

/// <summary>The words a refused Approve on a held release shows on its row (#1463).</summary>
public sealed class HeldReleaseRefusalWordsTests
{
    private static MediaGrabResult NotSent(string status, string? client, string? message = null)
        => new(false, null, "Show.S01E01.1080p", "Indexer", false, null, status, message, null, client);

    [Fact]
    public void A_client_that_is_not_answering_is_named_and_the_words_say_to_approve_again_or_use_another()
        => Assert.Equal(
            "Transmission is not answering, so Deluno is holding grabs to it. Approve again once it passes, or send it to another client.",
            AttentionEndpoints.NotSentWords(NotSent(GrabStatuses.Unready, "Transmission")));

    [Fact]
    public void A_switched_off_client_is_named_and_the_words_say_where_to_switch_it_on()
        => Assert.Equal(
            "qBittorrent is switched off, so nothing can be sent to it. Switch it on in Download clients, then approve again.",
            AttentionEndpoints.NotSentWords(NotSent(GrabStatuses.Paused, " qBittorrent ")));

    [Fact]
    public void Any_other_failure_carries_the_clients_own_message()
        => Assert.Equal(
            "SABnzbd did not take this release. Disk full. Approve again once that is fixed, or send it to another client.",
            AttentionEndpoints.NotSentWords(NotSent(GrabStatuses.Failed, "SABnzbd", "Disk full.")));

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void A_client_with_no_name_is_called_the_download_client(string? client)
        => Assert.StartsWith("The download client is not answering", AttentionEndpoints.NotSentWords(NotSent(GrabStatuses.Unready, client)));

    [Fact]
    public void A_refusal_joins_its_messages_and_skips_blank_ones()
        => Assert.Equal(
            "A better copy is on disk. Use force to replace it.",
            AttentionEndpoints.RefusalWords(
                new Dictionary<string, string[]> { ["force"] = ["A better copy is on disk.", " "], ["other"] = ["Use force to replace it."] },
                "Film.2020.1080p"));

    [Fact]
    public void A_refusal_with_no_words_says_the_release_could_not_be_sent()
        => Assert.Equal(
            "Film.2020.1080p could not be sent.",
            AttentionEndpoints.RefusalWords(new Dictionary<string, string[]> { ["force"] = [""] }, "Film.2020.1080p"));
}
