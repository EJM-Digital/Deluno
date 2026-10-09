using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Notifications.Contracts;
using Deluno.Notifications.Data;
using Deluno.Worker.HealthChecks.Checks;
using Moq;

namespace Deluno.Worker.Tests.HealthChecks.Checks;

/// <summary>
/// #1043: Health says, before a grab fails, when a tag leaves a connection
/// with no titles or titles with no indexer or download client -- and says
/// nothing at all to someone who never uses tags.
/// </summary>
public sealed class TagRoutingHealthCheckTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static IndexerItem Indexer(string id, string name, string tags = "") => new(
        id, name, "torznab", "public", "http://localhost", null, 1, "2000", tags, "both", true,
        "healthy", null, null, null, null, 0, null, null, Now, Now);

    private static DownloadClientItem Client(string id, string name, string tags = "") => new(
        id, name, "qbittorrent", "localhost", 8080, null, null, null, false, null,
        "movies", "tv", null, 1, true, "healthy", null, null, null, null, Now, Now, Tags: tags);

    private static NotificationWebhookItem Webhook(string id, string name, string tags = "") => new(
        id, name, "http://localhost/hook", "", true, null, null, Now, Now, Tags: tags);

    private static MediaTitleTags Title(string id, string name, bool monitored, params string[] tags) => new(id, name, monitored, tags);

    private static TagRoutingHealthCheck Build(
        IEnumerable<IndexerItem>? indexers = null,
        IEnumerable<DownloadClientItem>? clients = null,
        IEnumerable<NotificationWebhookItem>? webhooks = null,
        IEnumerable<MediaTitleTags>? movies = null,
        IEnumerable<MediaTitleTags>? shows = null)
    {
        var connections = new Mock<IConnectionsRepository>();
        connections.Setup(repo => repo.ListIndexersAsync(It.IsAny<CancellationToken>())).ReturnsAsync((indexers ?? []).ToArray());
        connections.Setup(repo => repo.ListDownloadClientsAsync(It.IsAny<CancellationToken>())).ReturnsAsync((clients ?? []).ToArray());
        var notifications = new Mock<INotificationRepository>();
        notifications.Setup(repo => repo.ListNotificationWebhooksAsync(It.IsAny<CancellationToken>())).ReturnsAsync((webhooks ?? []).ToArray());
        var tags = new Mock<IMediaTagStore>();
        tags.Setup(store => store.ListAllTitleTagsAsync(MediaKind.Movie, It.IsAny<CancellationToken>())).ReturnsAsync((movies ?? []).ToArray());
        tags.Setup(store => store.ListAllTitleTagsAsync(MediaKind.Series, It.IsAny<CancellationToken>())).ReturnsAsync((shows ?? []).ToArray());
        return new TagRoutingHealthCheck(connections.Object, notifications.Object, tags.Object);
    }

    [Fact]
    public async Task A_setup_that_never_uses_tags_gets_no_warnings_at_all()
    {
        var check = Build(
            indexers: [Indexer("i1", "NZBgeek")],
            clients: [Client("c1", "qBit")],
            webhooks: [Webhook("w1", "Discord")],
            movies: [Title("m1", "Sintel", true), Title("m2", "Tears of Steel", true, "kidz")]);

        Assert.Empty(await check.RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_tagged_download_client_no_title_uses_is_named_in_plain_words()
    {
        var check = Build(
            clients: [Client("c1", "Kids qBit", "kidz"), Client("c2", "Main qBit")],
            movies: [Title("m1", "Sintel", true)]);

        var outcome = Assert.Single(await check.RunAsync(CancellationToken.None));

        Assert.Equal("tags:unused:download-client:c1", outcome.Id);
        Assert.Equal(HealthCheckStatuses.Problem, outcome.Status);
        Assert.Equal("The download client Kids qBit only serves titles tagged kidz, and no title has that tag.", outcome.Message);
        Assert.Contains("/indexers/download-clients", outcome.DetailsJson);
    }

    [Fact]
    public async Task A_tagged_indexer_and_notification_with_no_matching_title_are_each_reported()
    {
        var check = Build(
            indexers: [Indexer("i1", "Private Tracker", "kidz")],
            webhooks: [Webhook("w1", "Family Discord", "kidz")],
            movies: [Title("m1", "Sintel", true, "adults")]);

        var outcomes = await check.RunAsync(CancellationToken.None);

        Assert.Contains(outcomes, item => item.Id == "tags:unused:indexer:i1"
            && item.Message == "The indexer Private Tracker only serves titles tagged kidz, and no title has that tag.");
        Assert.Contains(outcomes, item => item.Id == "tags:unused:notification:w1"
            && item.Message.StartsWith("The notification connection Family Discord only sends for titles tagged kidz"));
    }

    [Fact]
    public async Task A_tagged_connection_a_title_matches_is_not_reported_and_an_empty_library_is_not_nagged()
    {
        var used = Build(
            clients: [Client("c1", "Kids qBit", "kidz")],
            movies: [Title("m1", "Frozen", true, "KIDZ")]);
        Assert.Empty(await used.RunAsync(CancellationToken.None));

        var empty = Build(clients: [Client("c1", "Kids qBit", "kidz")]);
        Assert.Empty(await empty.RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Monitored_titles_no_tagged_download_client_can_take_are_counted_and_linked()
    {
        var check = Build(
            clients: [Client("c1", "Kids qBit", "kidz")],
            movies:
            [
                Title("m1", "Frozen", true, "kidz"),
                Title("m2", "Sintel", true),
                Title("m3", "Big Buck Bunny", true, "adults"),
                Title("m4", "Unwatched", false)
            ]);

        var outcome = Assert.Single(await check.RunAsync(CancellationToken.None), item => item.Id == "tags:no-download-client:movies");

        Assert.Contains("2 monitored titles in movies can't use any download client because of tags: Sintel, Big Buck Bunny.", outcome.Message);
        Assert.Contains("\"link\":\"/movies\"", outcome.DetailsJson);
        Assert.Contains("\"count\":2", outcome.DetailsJson);
    }

    [Fact]
    public async Task An_untagged_client_keeps_every_title_served_and_a_tagged_one_is_preferred_not_required()
    {
        var check = Build(
            clients: [Client("c1", "Kids qBit", "kidz"), Client("c2", "Main qBit")],
            movies: [Title("m1", "Frozen", true, "kidz"), Title("m2", "Sintel", true)]);

        var outcomes = await check.RunAsync(CancellationToken.None);

        Assert.DoesNotContain(outcomes, item => item.Id.StartsWith("tags:no-"));
    }

    [Fact]
    public async Task Monitored_shows_with_no_eligible_indexer_are_reported_separately_from_movies()
    {
        var check = Build(
            indexers: [Indexer("i1", "Anime Tracker", "anime")],
            movies: [Title("m1", "Sintel", true, "anime")],
            shows: [Title("s1", "Severance", true)]);

        var outcomes = await check.RunAsync(CancellationToken.None);

        var outcome = Assert.Single(outcomes, item => item.Id.StartsWith("tags:no-indexer"));
        Assert.Equal("tags:no-indexer:TV-shows", outcome.Id);
        Assert.Contains("1 monitored title in TV shows can't use any indexer because of tags: Severance.", outcome.Message);
        Assert.Contains("\"link\":\"/tv\"", outcome.DetailsJson);
    }

    [Fact]
    public async Task Two_tagged_clients_nothing_uses_read_differently()
    {
        var check = Build(
            clients: [Client("c1", "Kids qBit", "kidz"), Client("c2", "Teens qBit", "kidz")],
            movies: [Title("m1", "Sintel", true)]);

        var outcomes = await check.RunAsync(CancellationToken.None);

        SameProblemReadsDifferentlyPerItemTests.AssertEachReadsDifferently(
            outcomes.Where(item => item.Id.StartsWith("tags:unused:", StringComparison.Ordinal)).ToArray());
    }
}
