using Deluno.Connections.Data;
using Deluno.Integrations.MediaServers;
using Microsoft.Extensions.Logging;

namespace Deluno.Retention;

/// <summary>
/// Keeps one "Leaving soon" collection per media type on each server a rule
/// reads, holding everything any enabled rule is about to remove (#521).
///
/// <para><b>Deluno is the truth.</b> The collection is made to hold exactly the
/// marked titles each time: what was kept, stopped matching or was removed
/// leaves, and anything added or taken out by hand on the server is put back.
/// Nothing is created on a server until a title there is Leaving soon, and an
/// emptied collection is deleted.</para>
///
/// <para><b>Never in the way.</b> A server that cannot be reached is recorded
/// against that server and tried again next time. Removal decisions do not wait
/// on this; they follow the rule's own checks.</para>
/// </summary>
public sealed class RetentionCollectionService(
    IRetentionRepository repository,
    IMediaServerRepository servers,
    IMediaServerClientRegistry clients,
    TimeProvider timeProvider,
    ILogger<RetentionCollectionService> logger)
{
    public const string CollectionName = "Leaving soon";

    public static string Description(string mediaType)
        => $"{(mediaType == "tv" ? "Shows, seasons and episodes" : "Films")} Deluno will remove soon under a retention rule. " +
           "Open Retention in Deluno to see why each one is here, and keep any you want.";

    public async Task SyncAsync(CancellationToken cancellationToken)
    {
        var rules = (await repository.ListRulesAsync(cancellationToken)).Where(rule => rule.IsEnabled).ToDictionary(rule => rule.Id, StringComparer.OrdinalIgnoreCase);
        var marks = await repository.ListMarksAsync(null, cancellationToken);
        var states = (await repository.ListCollectionStatesAsync(cancellationToken)).ToDictionary(state => (state.ServerId, state.MediaType));
        var connections = (await servers.ListAsync(cancellationToken)).ToDictionary(server => server.Id, StringComparer.OrdinalIgnoreCase);

        // Every server an enabled rule reads, and every server Deluno has kept a
        // collection on before: one that no rule reads any more is emptied.
        var targets = rules.Values
            .SelectMany(rule => rule.MediaServerIds.Select(serverId => (ServerId: serverId, rule.MediaType)))
            .Concat(states.Keys)
            .Distinct()
            .ToArray();

        foreach (var (serverId, mediaType) in targets)
        {
            if (!connections.TryGetValue(serverId, out var server) || !server.IsEnabled || clients.Find(server.Kind) is not { } client)
            {
                continue;
            }

            var members = marks
                .Where(mark => mark.MediaType == mediaType && rules.TryGetValue(mark.RuleId, out var rule) && rule.MediaServerIds.Contains(serverId, StringComparer.OrdinalIgnoreCase))
                .SelectMany(mark => mark.ServerItems)
                .Where(item => string.Equals(item.ServerId, serverId, StringComparison.OrdinalIgnoreCase))
                .DistinctBy(item => item.ItemId, StringComparer.Ordinal)
                .Select(item => new MediaServerCollectionMember(item.ItemId, item.Level, item.SectionId))
                .ToArray();

            states.TryGetValue((serverId, mediaType), out var previous);
            // Nothing leaving, and nothing there to empty: leave the server alone.
            if (members.Length == 0 && (previous is null || (previous.Members == 0 && previous.Problem is null)))
            {
                continue;
            }

            var attempted = timeProvider.GetUtcNow();
            MediaServerAnswer<MediaServerCollectionSync> answer;
            try
            {
                answer = await client.SyncCollectionAsync(
                    server,
                    new MediaServerCollectionRequest(mediaType, CollectionName, Description(mediaType), members, server.LeavingSoonOnHome),
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Leaving soon collection on {Server} could not be updated", server.Name);
                answer = new MediaServerAnswer<MediaServerCollectionSync>.Unreachable($"Deluno could not update the Leaving soon collection on {server.Name}.");
            }

            var state = answer switch
            {
                MediaServerAnswer<MediaServerCollectionSync>.Reported reported => new RetentionCollectionState(serverId, mediaType, reported.Value.Members, reported.Value.Skipped, null, attempted, attempted),
                MediaServerAnswer<MediaServerCollectionSync>.Unreachable unreachable => new RetentionCollectionState(serverId, mediaType, members.Length, 0, unreachable.Reason, attempted, null),
                MediaServerAnswer<MediaServerCollectionSync>.NotSupported notSupported => new RetentionCollectionState(serverId, mediaType, members.Length, 0, notSupported.Reason, attempted, null),
                _ => null
            };

            if (state is not null)
            {
                await repository.SaveCollectionStateAsync(state, cancellationToken);
            }
        }
    }

    /// <summary>Sync, but never let a failure reach the caller: the check or keep it follows has already happened.</summary>
    public async Task TrySyncAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SyncAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Leaving soon collections could not be brought into line");
        }
    }
}
