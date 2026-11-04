using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Notifications.Contracts;
using Deluno.Notifications.Data;
using Deluno.Worker.HealthChecks.Checks;
using Moq;

namespace Deluno.Worker.Tests.HealthChecks.Checks;

/// <summary>#1043: how the tag rows read for one, two and three tags, for a few stranded titles and for many, and for each connection kind.</summary>
public sealed class TagRoutingHealthCheckWordsTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static IndexerItem Indexer(string id, string name, string tags = "", bool enabled = true) => new(
        id, name, "torznab", "public", "http://localhost", null, 1, "2000", tags, "both", enabled,
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

    [Theory]
    [InlineData("kidz", "kidz", "that tag")]
    [InlineData("kidz, family", "kidz or family", "any of those tags")]
    [InlineData("kidz,family , 4k", "kidz, family or 4k", "any of those tags")]
    public async Task The_tags_are_listed_in_plain_words_whatever_their_number(string tags, string words, string thatTag)
    {
        var check = Build(
            webhooks: [Webhook("w1", "Family Discord", tags)],
            movies: [Title("m1", "Sintel", true, "adults")]);

        var outcome = Assert.Single(await check.RunAsync(CancellationToken.None));

        Assert.Equal($"The notification connection Family Discord only sends for titles tagged {words}, and no title has {thatTag}.", outcome.Message);
        Assert.Equal($"Nothing in the library carries {words}, so The notification connection Family Discord is never used.", outcome.Cause);
        Assert.Equal("Tag a title with it, or remove the tag from Family Discord.", outcome.Fix);
        Assert.Equal("The notification connection Family Discord has no titles", outcome.Name);
        Assert.Equal(HealthCheckCategories.Tags, outcome.Category);
        Assert.Contains("\"link\":\"/settings/notifications\"", outcome.DetailsJson);
        Assert.Contains("\"kind\":\"notification\"", outcome.DetailsJson);
        Assert.Contains("\"connectionId\":\"w1\"", outcome.DetailsJson);
    }

    [Fact]
    public async Task A_connection_whose_tag_some_title_carries_is_not_reported_even_in_a_different_case()
    {
        var check = Build(
            indexers: [Indexer("i1", "Private Tracker", "Kidz")],
            movies: [Title("m1", "Sintel", true, "kidz")]);

        Assert.Empty(await check.RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_library_with_no_titles_yet_is_not_nagged_about_tags_nobody_could_have_used()
    {
        var check = Build(indexers: [Indexer("i1", "Private Tracker", "kidz")]);

        Assert.Empty(await check.RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_disabled_connection_is_left_alone_even_when_it_is_the_only_tagged_one()
    {
        var check = Build(
            indexers: [Indexer("i1", "Old Tracker", "kidz", enabled: false)],
            movies: [Title("m1", "Sintel", true, "adults")]);

        Assert.Empty(await check.RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task One_stranded_title_is_named_in_the_singular()
    {
        var check = Build(
            indexers: [Indexer("i1", "Private Tracker", "kidz")],
            movies: [Title("m1", "Sintel", true, "adults"), Title("m2", "Elephants Dream", false, "adults")]);

        var outcome = Assert.Single(await check.RunAsync(CancellationToken.None), item => item.Id.StartsWith("tags:no-indexer", StringComparison.Ordinal));

        Assert.Equal("tags:no-indexer:movies", outcome.Id);
        Assert.Equal("Some movies have no indexer because of tags", outcome.Name);
        Assert.Equal("1 monitored title in movies can't use any indexer because of tags: Sintel.", outcome.Message);
        Assert.Equal("Every indexer is tagged, and none shares a tag with it.", outcome.Cause);
        Assert.Equal("Tag it for one of your indexers, or add an indexer with no tags.", outcome.Fix);
        Assert.Contains("\"count\":1", outcome.DetailsJson);
        Assert.Contains("\"link\":\"/movies\"", outcome.DetailsJson);
    }

    [Fact]
    public async Task Exactly_three_stranded_titles_are_all_named_and_more_than_three_say_how_many_are_left_out()
    {
        string[] names = ["Alpha", "Bravo", "Charlie", "Delta", "Echo"];
        var three = Build(
            clients: [Client("c1", "Kids qBit", "kidz")],
            shows: names.Take(3).Select((name, i) => Title("s" + i, name, true, "adults")));
        var five = Build(
            clients: [Client("c1", "Kids qBit", "kidz")],
            shows: names.Select((name, i) => Title("s" + i, name, true, "adults")));

        var threeOutcome = Assert.Single(await three.RunAsync(CancellationToken.None), item => item.Id.StartsWith("tags:no-download-client", StringComparison.Ordinal));
        var fiveOutcome = Assert.Single(await five.RunAsync(CancellationToken.None), item => item.Id.StartsWith("tags:no-download-client", StringComparison.Ordinal));

        Assert.Equal("tags:no-download-client:TV-shows", threeOutcome.Id);
        Assert.Equal("3 monitored titles in TV shows can't use any download client because of tags: Alpha, Bravo, Charlie.", threeOutcome.Message);
        Assert.Equal("Every download client is tagged, and none shares a tag with them.", threeOutcome.Cause);
        Assert.Equal("Tag them for one of your download clients, or add a download client with no tags.", threeOutcome.Fix);
        Assert.Equal("5 monitored titles in TV shows can't use any download client because of tags: Alpha, Bravo, Charlie and 2 more.", fiveOutcome.Message);
        Assert.Contains("\"count\":5", fiveOutcome.DetailsJson);
        Assert.Contains("\"link\":\"/tv\"", fiveOutcome.DetailsJson);
    }

    [Fact]
    public async Task A_title_one_connection_can_serve_is_not_stranded_and_an_unmonitored_one_is_never_counted()
    {
        var check = Build(
            clients: [Client("c1", "Kids qBit", "kidz"), Client("c2", "Main qBit")],
            movies: [Title("m1", "Sintel", true, "adults"), Title("m2", "Elephants Dream", true, "kidz")]);

        var outcomes = await check.RunAsync(CancellationToken.None);

        Assert.DoesNotContain(outcomes, item => item.Id.StartsWith("tags:no-", StringComparison.Ordinal));
    }
}
