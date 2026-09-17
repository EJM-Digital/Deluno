using Deluno.Retention;

namespace Deluno.Worker.Tests.Retention;

/// <summary>Retention storage in a dictionary, so the lifecycle can be followed check by check.</summary>
internal sealed class InMemoryRetentionRepository : IRetentionRepository
{
    private readonly Dictionary<string, RetentionRule> _rules = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Rule, string Entity), RetentionMark> _marks = [];
    private readonly List<RetentionKeep> _keeps = [];
    private readonly Dictionary<string, RetentionEpisodeRemoval> _episodes = new(StringComparer.OrdinalIgnoreCase);

    public List<RetentionRun> Runs { get; } = [];

    public void Add(RetentionRule rule) => _rules[rule.Id] = rule;

    public Task<IReadOnlyList<RetentionRule>> ListRulesAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<RetentionRule>>(_rules.Values.ToArray());

    public Task<RetentionRule?> GetRuleAsync(string id, CancellationToken cancellationToken)
        => Task.FromResult(_rules.GetValueOrDefault(id));

    public Task<RetentionRule?> SaveRuleAsync(string? id, SaveRetentionRuleRequest request, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<bool> DeleteRuleAsync(string id, CancellationToken cancellationToken)
        => Task.FromResult(_rules.Remove(id));

    public Task MarkCheckedAsync(string ruleId, DateTimeOffset when, CancellationToken cancellationToken)
    {
        if (_rules.TryGetValue(ruleId, out var rule)) _rules[ruleId] = rule with { LastCheckedUtc = when };
        return Task.CompletedTask;
    }

    public Task SaveRunAsync(RetentionRun run, CancellationToken cancellationToken)
    {
        Runs.Add(run);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RetentionRun>> ListRunsAsync(string ruleId, int take, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<RetentionRun>>(Runs.Where(run => run.RuleId == ruleId).Reverse().Take(take).ToArray());

    public Task<RetentionRun?> GetRunAsync(string runId, CancellationToken cancellationToken)
        => Task.FromResult(Runs.FirstOrDefault(run => run.Id == runId));

    public Task<IReadOnlyList<RetentionMark>> ListMarksAsync(string? ruleId, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<RetentionMark>>(_marks.Values.Where(mark => ruleId is null || mark.RuleId == ruleId).ToArray());

    public Task<IReadOnlyList<RetentionMark>> ListMarksForTitleAsync(string entityId, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<RetentionMark>>(_marks.Values.Where(mark => mark.EntityId == entityId || mark.ShowId == entityId).ToArray());

    public Task UpsertMarkAsync(RetentionMark mark, CancellationToken cancellationToken)
    {
        _marks[(mark.RuleId, mark.EntityId)] = _marks.TryGetValue((mark.RuleId, mark.EntityId), out var existing)
            ? mark with { FirstMatchedUtc = existing.FirstMatchedUtc }
            : mark;
        return Task.CompletedTask;
    }

    public Task DeleteMarkAsync(string ruleId, string entityId, CancellationToken cancellationToken)
    {
        _marks.Remove((ruleId, entityId));
        return Task.CompletedTask;
    }

    public Task<int> DeleteMarksForEntityAsync(string entityId, CancellationToken cancellationToken)
    {
        var keys = _marks.Keys.Where(key => key.Entity == entityId).ToArray();
        foreach (var key in keys) _marks.Remove(key);
        return Task.FromResult(keys.Length);
    }

    public Task<IReadOnlyList<RetentionKeep>> ListKeepsAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<RetentionKeep>>(_keeps.ToArray());

    public Task<RetentionKeep> SaveKeepAsync(string entityId, string level, string title, string? ruleId, string? note, CancellationToken cancellationToken)
    {
        _keeps.RemoveAll(keep => keep.EntityId == entityId && keep.RuleId == ruleId);
        var keep = new RetentionKeep(Guid.NewGuid().ToString("N"), entityId, level, title, ruleId, ruleId is null ? null : _rules.GetValueOrDefault(ruleId)?.Name, note, DateTimeOffset.UnixEpoch);
        _keeps.Add(keep);
        return Task.FromResult(keep);
    }

    public Task<RetentionKeep?> DeleteKeepAsync(string id, CancellationToken cancellationToken)
    {
        var keep = _keeps.FirstOrDefault(item => item.Id == id);
        if (keep is not null) _keeps.Remove(keep);
        return Task.FromResult(keep);
    }

    public Task SaveEpisodeRemovalAsync(RetentionEpisodeRemoval removal, CancellationToken cancellationToken)
    {
        _episodes[removal.EpisodeId] = removal;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RetentionEpisodeRemoval>> ListEpisodeRemovalsAsync(string seriesId, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<RetentionEpisodeRemoval>>(_episodes.Values.Where(item => item.SeriesId == seriesId).ToArray());

    public Task<RetentionEpisodeRemoval?> DeleteEpisodeRemovalAsync(string episodeId, CancellationToken cancellationToken)
        => Task.FromResult(_episodes.Remove(episodeId, out var removal) ? removal : null);

    private readonly Dictionary<(string, string), RetentionCollectionState> _collections = new();

    public IReadOnlyDictionary<(string ServerId, string MediaType), RetentionCollectionState> Collections => _collections;

    public Task<IReadOnlyList<RetentionCollectionState>> ListCollectionStatesAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<RetentionCollectionState>>(_collections.Values.ToArray());

    public Task SaveCollectionStateAsync(RetentionCollectionState state, CancellationToken cancellationToken)
    {
        var previous = _collections.GetValueOrDefault((state.ServerId, state.MediaType));
        _collections[(state.ServerId, state.MediaType)] = state with { SyncedUtc = state.SyncedUtc ?? previous?.SyncedUtc };
        return Task.CompletedTask;
    }
}
