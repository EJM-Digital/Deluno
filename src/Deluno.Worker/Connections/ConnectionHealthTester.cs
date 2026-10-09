using System.Diagnostics;
using Deluno.Connections;
using Deluno.Connections.Data;
using Deluno.Connections.Versions;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.MediaServers;
using Deluno.Integrations.Metadata;
using Deluno.Integrations.Seerr;
using Deluno.Intake.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Worker.Intake;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Worker.Connections;

/// <summary>
/// The one place a connection is tested for real, whichever way the test was asked for (#1098): the regular
/// check, Test all, or a per-connection Test button. Every result is stored and pushed as
/// <c>ConnectionChecked</c> by <see cref="IConnectionCheckRecorder"/>, so "last checked" is the real last check.
/// </summary>
public sealed class ConnectionHealthTester(
    IServiceProvider services,
    IConnectionsRepository connections,
    IMediaServerRepository mediaServers,
    ISeerrRepository seerrServers,
    IProcessorRepository processors,
    IIntakeRepository intake,
    IConnectionCheckRecorder recorder,
    IConnectionVersionStore? versions = null) : IConnectionHealthTester
{
    /// <summary>One thing that can be tested: its identity and when it was last checked or used by a real call.</summary>
    private sealed record Target(string Kind, string Id, string Name, DateTimeOffset? LastTouchedUtc);

    public async Task<ConnectionCheckedEvent?> TestAsync(string kind, string id, CancellationToken cancellationToken)
    {
        var target = (await ListTargetsAsync(cancellationToken))
            .FirstOrDefault(item => string.Equals(item.Kind, kind, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
        return target is null ? null : await RunAsync(target, automatic: false, cancellationToken);
    }

    public async Task<IReadOnlyList<ConnectionCheckedEvent>> TestAllAsync(CancellationToken cancellationToken)
        => await RunManyAsync(await ListTargetsAsync(cancellationToken), automatic: false, cancellationToken);

    public async Task<IReadOnlyList<ConnectionCheckedEvent>> TestStaleAsync(DateTimeOffset staleBeforeUtc, CancellationToken cancellationToken)
    {
        var targets = await ListTargetsAsync(cancellationToken);
        var stale = targets.Where(target => target.LastTouchedUtc is null || target.LastTouchedUtc < staleBeforeUtc).ToArray();
        var results = await RunManyAsync(stale, automatic: true, cancellationToken);

        // #1200: a processor that was used recently is not tested again, but its version is still read on every
        // regular pass, so an upgrade of the processor shows without waiting for a quiet spell.
        foreach (var target in targets.Where(target => target.Kind == ConnectionKinds.Processor && !stale.Contains(target)))
        {
            try
            {
                var item = await processors.GetProcessorConnectionAsync(target.Id, cancellationToken);
                if (item is not null)
                {
                    using var scope = services.CreateScope();
                    var processorService = scope.ServiceProvider.GetRequiredService<IProcessorConnectionService>();
                    await versions.RememberVersionAsync(ConnectionKinds.Processor, item.Id, ProcessorProjects.ProjectFor(item), await processorService.ReadVersionAsync(item, cancellationToken), cancellationToken);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Reading a version never fails the pass.
            }
        }

        return results;
    }

    private async Task<IReadOnlyList<ConnectionCheckedEvent>> RunManyAsync(
        IReadOnlyList<Target> targets,
        bool automatic,
        CancellationToken cancellationToken)
    {
        var results = new List<ConnectionCheckedEvent>(targets.Count);
        using var gate = new SemaphoreSlim(ConnectionCheckRules.Parallelism);
        var tasks = targets.Select(async target =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var result = await RunAsync(target, automatic, cancellationToken);
                if (result is not null)
                {
                    lock (results)
                    {
                        results.Add(result);
                    }
                }
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks);
        return results;
    }

    private async Task<IReadOnlyList<Target>> ListTargetsAsync(CancellationToken cancellationToken)
    {
        var checks = (await recorder.ListAsync(cancellationToken))
            .ToDictionary(item => $"{item.Kind}:{item.Id}", item => item.CheckedUtc, StringComparer.OrdinalIgnoreCase);

        DateTimeOffset? Latest(string kind, string id, DateTimeOffset? own)
        {
            checks.TryGetValue($"{kind}:{id}", out var recorded);
            if (own is null)
            {
                return checks.ContainsKey($"{kind}:{id}") ? recorded : null;
            }

            return recorded > own.Value ? recorded : own;
        }

        var targets = new List<Target>();
        targets.AddRange((await connections.ListIndexersAsync(cancellationToken))
            .Where(item => item.IsEnabled)
            .Select(item => new Target(ConnectionKinds.Indexer, item.Id, item.Name, Latest(ConnectionKinds.Indexer, item.Id, item.LastHealthTestUtc))));
        targets.AddRange((await connections.ListDownloadClientsAsync(cancellationToken))
            .Where(item => item.IsEnabled)
            .Select(item => new Target(ConnectionKinds.DownloadClient, item.Id, item.Name, Latest(ConnectionKinds.DownloadClient, item.Id, item.LastHealthTestUtc))));
        targets.AddRange((await processors.ListProcessorConnectionsAsync(cancellationToken))
            .Where(item => item.IsEnabled)
            .Select(item => new Target(ConnectionKinds.Processor, item.Id, item.Name, Latest(ConnectionKinds.Processor, item.Id, item.LastHealthTestUtc))));
        targets.AddRange((await mediaServers.ListAsync(cancellationToken))
            .Where(item => item.IsEnabled)
            .Select(item => new Target(ConnectionKinds.MediaServer, item.Id, item.Name, Latest(ConnectionKinds.MediaServer, item.Id, item.LastHealthTestUtc))));
        targets.AddRange((await seerrServers.ListAsync(cancellationToken))
            .Where(item => item.IsEnabled)
            .Select(item => new Target(ConnectionKinds.Requests, item.Id, item.Name, Latest(ConnectionKinds.Requests, item.Id, item.LastHealthTestUtc))));

        var metadata = services.GetService<IMetadataProvider>();
        var metadataStatus = metadata is null ? null : await metadata.GetStatusAsync(cancellationToken);
        if (metadataStatus is { IsConfigured: true } status)
        {
            targets.Add(new Target(ConnectionKinds.Metadata, "metadata", status.Provider, Latest(ConnectionKinds.Metadata, "metadata", null)));
        }

        // Lists are one connection per provider family; the first enabled list of a family stands for it.
        foreach (var family in (await intake.ListIntakeSourcesAsync(cancellationToken))
                     .Where(item => item.IsEnabled)
                     .GroupBy(item => ListProviderFamilies.KeyFor(item.Provider), StringComparer.OrdinalIgnoreCase))
        {
            var first = family.First();
            var lastSync = family.Max(item => item.LastSyncUtc);
            var checkedUtc = Latest(ConnectionKinds.Lists, family.Key, lastSync);
            targets.Add(new Target(ConnectionKinds.Lists, family.Key, ListProviderFamilies.NameFor(first.Provider), checkedUtc));
        }

        return targets;
    }

    private async Task<ConnectionCheckedEvent?> RunAsync(Target target, bool automatic, CancellationToken cancellationToken)
    {
        try
        {
            // A test of one connection must never stop the next: its own failure is the answer.
            using var scope = services.CreateScope();
            var scoped = scope.ServiceProvider;
            switch (target.Kind)
            {
                case ConnectionKinds.Indexer:
                {
                    var item = (await connections.ListIndexersAsync(cancellationToken))
                        .FirstOrDefault(row => string.Equals(row.Id, target.Id, StringComparison.OrdinalIgnoreCase));
                    if (item is null)
                    {
                        return null;
                    }

                    var result = await ConnectionsEndpointRouteBuilderExtensions.RunStoredIndexerTestAsync(item, scoped, automatic, cancellationToken);
                    // No version for an indexer: Deluno replaces Prowlarr and Jackett, so it never reports theirs (James, 3 Oct 2026).
                    return result is null ? null : await LatestAsync(target, cancellationToken);
                }
                case ConnectionKinds.DownloadClient:
                {
                    var item = (await connections.ListDownloadClientsAsync(cancellationToken))
                        .FirstOrDefault(row => string.Equals(row.Id, target.Id, StringComparison.OrdinalIgnoreCase));
                    if (item is null)
                    {
                        return null;
                    }

                    var result = await ConnectionsEndpointRouteBuilderExtensions.RunStoredDownloadClientTestAsync(item, scoped, automatic, cancellationToken);
                    // #1117: a client that answered also says which version it runs, read the way its poll reads it.
                    if (versions is not null && result is { HealthStatus: "healthy" } && scoped.GetService<IDownloadClientRegistry>() is { } registry && registry.TryGet(item.Protocol, out var implementation))
                    {
                        try
                        {
                            var snapshot = await implementation.GetSnapshotAsync(item, DateTimeOffset.UtcNow, cancellationToken);
                            await versions.RememberVersionAsync(ConnectionKinds.DownloadClient, item.Id, ConnectionProjects.ForDownloadClient(item.Protocol), snapshot?.ClientVersion, cancellationToken);
                        }
                        catch (Exception exception) when (exception is not OperationCanceledException)
                        {
                            // Reading the version never fails the check.
                        }
                    }

                    return result is null ? null : await LatestAsync(target, cancellationToken);
                }
                case ConnectionKinds.Processor:
                {
                    var item = await processors.GetProcessorConnectionAsync(target.Id, cancellationToken);
                    if (item is null)
                    {
                        return null;
                    }

                    var processorService = scoped.GetRequiredService<IProcessorConnectionService>();
                    var result = await processorService.TestAsync(item, cancellationToken);
                    await processors.RecordProcessorConnectionHealthAsync(item.Id, result.Status, result.Message, cancellationToken);
                    // The processor service stores its version whenever it reads the processor's health (#1200).
                    // #1144: a connection still called "Processor on <machine>" takes the processor's own name once it publishes one.
                    if (result.IsReachable)
                    {
                        item = await Deluno.Platform.ProcessorAutoNames.RenameIfGeneratedAsync(item, processors, processorService, cancellationToken);
                    }

                    return await recorder.RecordAsync(target.Kind, item.Id, item.Name, result.IsReachable, result.LatencyMs, result.Message, cancellationToken);
                }
                case ConnectionKinds.MediaServer:
                {
                    var item = await mediaServers.GetAsync(target.Id, cancellationToken);
                    var client = item is null ? null : scoped.GetRequiredService<IMediaServerClientRegistry>().Find(item.Kind);
                    if (item is null || client is null)
                    {
                        return null;
                    }

                    var result = await client.TestAsync(item, cancellationToken);
                    await mediaServers.RecordHealthAsync(item.Id, result, cancellationToken);
                    await versions.RememberVersionAsync(ConnectionKinds.MediaServer, item.Id, ConnectionProjects.ForMediaServer(item.Kind), result.Version, cancellationToken);
                    return await recorder.RecordAsync(target.Kind, item.Id, item.Name, result.Ok, result.LatencyMs, result.Message, cancellationToken);
                }
                case ConnectionKinds.Requests:
                {
                    var item = await seerrServers.GetAsync(target.Id, cancellationToken);
                    if (item is null)
                    {
                        return null;
                    }

                    var result = await scoped.GetRequiredService<ISeerrClient>().TestAsync(item, cancellationToken);
                    await seerrServers.RecordHealthAsync(item.Id, result, cancellationToken);
                    await versions.RememberVersionAsync(ConnectionKinds.Requests, item.Id, ConnectionProjects.Seerr, result.Version, cancellationToken);
                    return await recorder.RecordAsync(target.Kind, item.Id, item.Name, result.Ok, result.LatencyMs, result.Message, cancellationToken);
                }
                case ConnectionKinds.Metadata:
                {
                    var provider = scoped.GetRequiredService<IMetadataProvider>();
                    var started = Stopwatch.GetTimestamp();
                    var status = await provider.CheckReachabilityAsync(cancellationToken);
                    var elapsed = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    var answered = status.LastFailure is null;
                    return await recorder.RecordAsync(
                        target.Kind,
                        target.Id,
                        status.Provider,
                        answered,
                        elapsed,
                        answered ? status.Message : status.LastFailure?.Summary ?? status.Message,
                        cancellationToken);
                }
                case ConnectionKinds.Lists:
                {
                    var source = (await intake.ListIntakeSourcesAsync(cancellationToken))
                        .Where(item => item.IsEnabled && string.Equals(ListProviderFamilies.KeyFor(item.Provider), target.Id, StringComparison.OrdinalIgnoreCase))
                        .OrderBy(item => item.CreatedUtc)
                        .FirstOrDefault();
                    if (source is null)
                    {
                        return null;
                    }

                    var result = await scoped.GetRequiredService<IntakeSyncService>().TestSourceAsync(source, cancellationToken);
                    return await recorder.RecordAsync(target.Kind, target.Id, target.Name, result.Ok, result.LatencyMs, result.Message, cancellationToken);
                }
                default:
                    return null;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // The test itself blew up: that is still an answer a person should see, not a silent gap.
            return await recorder.RecordAsync(target.Kind, target.Id, target.Name, false, null, exception.Message, cancellationToken);
        }
    }

    private async Task<ConnectionCheckedEvent?> LatestAsync(Target target, CancellationToken cancellationToken)
    {
        var item = (await recorder.ListAsync(cancellationToken))
            .FirstOrDefault(row => string.Equals(row.Kind, target.Kind, StringComparison.OrdinalIgnoreCase)
                && string.Equals(row.Id, target.Id, StringComparison.OrdinalIgnoreCase));
        return item is null
            ? null
            : new ConnectionCheckedEvent(item.Kind, item.Id, item.Name, item.Ok, item.Slow, item.Message, item.CheckedUtc.UtcDateTime.ToString("O"));
    }
}
