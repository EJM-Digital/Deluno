using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>#1240: a move to a keep-seeding category counts as done only when the client had the torrent before and reports it in the new category after.</summary>
public sealed class ClientCategoryMoveBranchesTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static DownloadClientItem Client()
        => new("c1", "qBit", "qbittorrent", "localhost", 8080, null, null, null, false, null, "movies", "tv", null, 1, true, "healthy", null, null, null, Now, Now, Now);

    private static DownloadClientActionResult Ok() => new("c1", "hash", DownloadClientActions.SetCategory, true, "told");

    private static async Task<(DownloadClientActionResult Result, int Sets)> RunAsync(params ClientCategoryReading[] readings)
    {
        var next = 0;
        var sets = 0;
        var result = await ClientCategoryMove.RunAsync(
            Client(), "hash", "seeding", "qBit",
            () => Task.FromResult(readings[Math.Min(next++, readings.Length - 1)]),
            _ => { sets++; return Task.FromResult(Ok()); });
        return (result, sets);
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData(" Seeding ", "seeding", true)]
    [InlineData("seeding", "SEEDING", true)]
    [InlineData("seeding", "movies", false)]
    [InlineData("", "seeding", false)]
    [InlineData(null, "seeding", false)]
    public void Two_categories_are_the_same_whatever_the_case_and_spaces(string? left, string? right, bool same)
    {
        Assert.Equal(same, ClientCategoryMove.SameCategory(left, right));
    }

    [Fact]
    public async Task A_client_that_does_not_have_the_torrent_moves_nothing_and_says_it_may_be_gone()
    {
        var (result, sets) = await RunAsync(new ClientCategoryReading(ClientHas.No, null));

        Assert.False(result.Succeeded);
        Assert.Equal("qBit has no download with that id, so nothing was moved. It may already have been removed there.", result.Message);
        Assert.Equal(0, sets);
    }

    [Fact]
    public async Task A_client_that_cannot_be_asked_moves_nothing_and_is_marked_unreachable()
    {
        var (result, sets) = await RunAsync(new ClientCategoryReading(ClientHas.Unknown, null));

        Assert.False(result.Succeeded);
        Assert.Equal("qBit could not be asked about this download, so nothing was changed.", result.Message);
        Assert.Equal(0, sets);
    }

    [Fact]
    public async Task A_torrent_already_in_the_category_is_a_success_without_asking_the_client_to_do_anything()
    {
        var (result, sets) = await RunAsync(new ClientCategoryReading(ClientHas.Yes, " Seeding "));

        Assert.True(result.Succeeded);
        Assert.Equal("qBit already has this download in seeding.", result.Message);
        Assert.Equal(0, sets);
    }

    [Fact]
    public async Task A_move_the_client_refuses_is_returned_as_it_was_and_not_checked_again()
    {
        var reads = 0;
        var refusal = new DownloadClientActionResult("c1", "hash", DownloadClientActions.SetCategory, false, "no way");

        var result = await ClientCategoryMove.RunAsync(
            Client(), "hash", "seeding", "qBit",
            () => { reads++; return Task.FromResult(new ClientCategoryReading(ClientHas.Yes, "movies")); },
            _ => Task.FromResult(refusal));

        Assert.Same(refusal, result);
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task A_move_that_is_seen_afterwards_is_a_success()
    {
        var (result, sets) = await RunAsync(new ClientCategoryReading(ClientHas.Yes, "movies"), new ClientCategoryReading(ClientHas.Yes, "seeding"));

        Assert.True(result.Succeeded);
        Assert.Equal("qBit moved this download to seeding.", result.Message);
        Assert.Equal(1, sets);
    }

    [Theory]
    [InlineData("movies", "qBit still has this download in movies after being told to move it to seeding, so it was not moved.")]
    [InlineData("", "qBit still has this download in no category after being told to move it to seeding, so it was not moved.")]
    [InlineData(null, "qBit still has this download in no category after being told to move it to seeding, so it was not moved.")]
    public async Task A_move_that_is_not_seen_afterwards_says_where_the_torrent_still_is(string? still, string message)
    {
        var (result, _) = await RunAsync(new ClientCategoryReading(ClientHas.Yes, "other"), new ClientCategoryReading(ClientHas.Yes, still));

        Assert.False(result.Succeeded);
        Assert.Equal(message, result.Message);
    }

    [Fact]
    public async Task A_torrent_that_vanishes_after_the_move_is_not_called_moved()
    {
        var (result, _) = await RunAsync(new ClientCategoryReading(ClientHas.Yes, "movies"), new ClientCategoryReading(ClientHas.No, null));

        Assert.False(result.Succeeded);
        Assert.Equal("qBit no longer lists this download, so Deluno cannot say it was moved.", result.Message);
    }

    [Fact]
    public async Task A_client_that_cannot_confirm_the_move_is_not_believed()
    {
        var (result, _) = await RunAsync(new ClientCategoryReading(ClientHas.Yes, "movies"), new ClientCategoryReading(ClientHas.Unknown, null));

        Assert.False(result.Succeeded);
        Assert.Equal("qBit did not confirm the move, so Deluno cannot say it happened.", result.Message);
    }
}
