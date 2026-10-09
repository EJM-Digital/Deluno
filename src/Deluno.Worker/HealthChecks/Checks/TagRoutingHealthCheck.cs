using System.Text.Json;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Integrations.Search;
using Deluno.Notifications.Data;

namespace Deluno.Worker.HealthChecks.Checks;

/// <summary>
/// Two things a tag can quietly break, said before they bite (#1043):
///
/// <para>1. A tagged indexer, download client or notification connection
/// that no title in the library carries a matching tag for, so it will never
/// be used. One row per connection.</para>
///
/// <para>2. Monitored titles that no indexer, or no download client, can
/// serve because of tags. Radarr and Sonarr only find that out at grab time;
/// the eligibility test is the acquisition pipeline's own
/// (<see cref="DownloadClientTagMatch"/>, which forwards to
/// <see cref="TagRoutingMatch"/>, including <c>PreferTagged</c>), never a
/// second copy of it. One row per kind of connection and media type, with the
/// count and a link.</para>
///
/// <para>Tags are optional: with no tagged connection this reports nothing at
/// all, and a library with no titles yet is not nagged about tags nobody could
/// have used. Every row can be snoozed from Health like any other warning.</para>
/// </summary>
public sealed class TagRoutingHealthCheck(
    IConnectionsRepository connections,
    INotificationRepository notifications,
    IMediaTagStore mediaTags) : IHealthCheck
{
    public string Category => HealthCheckCategories.Tags;

    public async Task<IReadOnlyList<HealthCheckOutcome>> RunAsync(CancellationToken cancellationToken)
    {
        var indexers = (await connections.ListIndexersAsync(cancellationToken)).Where(item => item.IsEnabled).ToArray();
        var clients = (await connections.ListDownloadClientsAsync(cancellationToken)).Where(item => item.IsEnabled).ToArray();
        var webhooks = (await notifications.ListNotificationWebhooksAsync(cancellationToken)).Where(item => item.IsEnabled).ToArray();

        var anyTagged = indexers.Any(item => HasTags(item.Tags))
            || clients.Any(item => HasTags(item.Tags))
            || webhooks.Any(item => HasTags(item.Tags));
        if (!anyTagged)
        {
            return [];
        }

        var movies = await mediaTags.ListAllTitleTagsAsync(MediaKind.Movie, cancellationToken);
        var shows = await mediaTags.ListAllTitleTagsAsync(MediaKind.Series, cancellationToken);
        var outcomes = new List<HealthCheckOutcome>();

        // A library with nothing in it yet has no title that could carry a tag;
        // saying so would only nag a new setup.
        if (movies.Count + shows.Count > 0)
        {
            var heldTags = movies.Concat(shows)
                .SelectMany(title => title.Tags)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var indexer in indexers.Where(item => HasTags(item.Tags)))
            {
                AddUnused(outcomes, "indexer", indexer.Id, $"The indexer {indexer.Name}", "serves", indexer.Tags, heldTags,
                    "/indexers/indexers", $"Tag a title with it, or remove the tag from {indexer.Name}.");
            }

            foreach (var client in clients.Where(item => HasTags(item.Tags)))
            {
                AddUnused(outcomes, "download-client", client.Id, $"The download client {client.Name}", "serves", client.Tags, heldTags,
                    "/indexers/download-clients", $"Tag a title with it, or remove the tag from {client.Name}.");
            }

            foreach (var webhook in webhooks.Where(item => HasTags(item.Tags)))
            {
                AddUnused(outcomes, "notification", webhook.Id, $"The notification connection {webhook.Name}", "sends for", webhook.Tags, heldTags,
                    "/settings/notifications", $"Tag a title with it, or remove the tag from {webhook.Name}.");
            }
        }

        // Only tags can make this true: with no enabled connection of a kind at
        // all the problem is a missing connection, which other checks report.
        if (indexers.Length > 0)
        {
            bool IndexerFor(MediaTitleTags title) =>
                indexers.Any(item => DownloadClientTagMatch.IsEligible(item.Tags, title.Tags));
            AddUnserved(outcomes, "indexer", "movies", "/movies", movies, IndexerFor, "indexer");
            AddUnserved(outcomes, "indexer", "TV shows", "/tv", shows, IndexerFor, "indexer");
        }

        if (clients.Length > 0)
        {
            // The same choice the pipeline makes: eligible clients, tagged ones
            // preferred. A title is stranded when that leaves nothing.
            bool ClientFor(MediaTitleTags title) =>
                DownloadClientTagMatch.PreferTagged(
                        clients.Where(item => DownloadClientTagMatch.IsEligible(item.Tags, title.Tags)),
                        item => item.Tags,
                        title.Tags)
                    .Any();
            AddUnserved(outcomes, "download-client", "movies", "/movies", movies, ClientFor, "download client");
            AddUnserved(outcomes, "download-client", "TV shows", "/tv", shows, ClientFor, "download client");
        }

        return outcomes;
    }

    private static bool HasTags(string? tags) => !string.IsNullOrWhiteSpace(tags);

    private void AddUnused(
        List<HealthCheckOutcome> outcomes,
        string kind,
        string id,
        string subject,
        string verb,
        string connectionTags,
        HashSet<string> heldTags,
        string link,
        string fix)
    {
        var tags = SplitTags(connectionTags);
        if (tags.Any(heldTags.Contains))
        {
            return;
        }

        outcomes.Add(HealthCheckOutcome.Problem(
            $"tags:unused:{kind}:{id}",
            Category,
            $"{subject} has no titles",
            $"{subject} only {verb} titles tagged {JoinWords(tags)}, and no title has {(tags.Length == 1 ? "that tag" : "any of those tags")}.",
            cause: $"Nothing in the library carries {JoinWords(tags)}, so {subject} is never used.",
            fix: fix,
            detailsJson: JsonSerializer.Serialize(new { link, kind, connectionId = id })));
    }

    private void AddUnserved(
        List<HealthCheckOutcome> outcomes,
        string kind,
        string mediaLabel,
        string link,
        IReadOnlyList<MediaTitleTags> titles,
        Func<MediaTitleTags, bool> hasEligible,
        string connectionNoun)
    {
        var stranded = titles.Where(title => title.IsMonitored && !hasEligible(title)).ToArray();
        if (stranded.Length == 0)
        {
            return;
        }

        var names = string.Join(", ", stranded.Take(3).Select(title => title.Title));
        var more = stranded.Length > 3 ? $" and {stranded.Length - 3} more" : "";
        var count = stranded.Length == 1 ? "1 monitored title" : $"{stranded.Length} monitored titles";
        var these = stranded.Length == 1 ? "it" : "them";
        outcomes.Add(HealthCheckOutcome.Problem(
            $"tags:no-{kind}:{mediaLabel.Replace(' ', '-')}",
            Category,
            $"Some {mediaLabel} have no {connectionNoun} because of tags",
            $"{count} in {mediaLabel} can't use any {connectionNoun} because of tags: {names}{more}.",
            cause: $"Every {connectionNoun} is tagged, and none shares a tag with {these}.",
            fix: $"Tag {these} for one of your {connectionNoun}s, or add a {connectionNoun} with no tags.",
            detailsJson: JsonSerializer.Serialize(new { link, kind, count = stranded.Length })));
    }

    private static string[] SplitTags(string csv) =>
        csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string JoinWords(string[] tags) => tags.Length switch
    {
        1 => tags[0],
        2 => $"{tags[0]} or {tags[1]}",
        _ => $"{string.Join(", ", tags[..^1])} or {tags[^1]}"
    };
}
