using Deluno.Integrations.Seerr;
using Deluno.Retention;

namespace Deluno.Worker.Tests.Retention;

/// <summary>Seerr's requests and clearings, held in memory (#522).</summary>
internal sealed class FakeRetentionSeerr : IRetentionSeerr
{
    /// <summary>Null: no Seerr server is connected.</summary>
    public SeerrLibrary? Library { get; set; }

    public string? Unreachable { get; set; }

    public string? ClearProblem { get; set; }

    public List<(string EntityId, string MediaType, string? TmdbId)> Cleared { get; } = [];

    public Task<(RetentionSeerrIndex? Index, IReadOnlyList<string> Unreachable)> ReadAsync(CancellationToken cancellationToken)
        => Task.FromResult<(RetentionSeerrIndex?, IReadOnlyList<string>)>(
            (Library is null ? null : new RetentionSeerrIndex([Library]), Unreachable is null ? [] : [Unreachable]));

    public Task<string?> ClearAsync(string entityId, string mediaType, string title, string? tmdbId, string? tvdbId, CancellationToken cancellationToken)
    {
        if (Library is null)
        {
            return Task.FromResult<string?>(null);
        }

        Cleared.Add((entityId, mediaType, tmdbId));
        return Task.FromResult<string?>(ClearProblem is null ? "Cleared from Lab Seerr." : $"Lab Seerr could not be cleared yet ({ClearProblem}); Deluno tries again at the next check.");
    }

    public Task RetryAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<IReadOnlyList<RetentionSeerrCleanup>> ListCleanupsAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<RetentionSeerrCleanup>>([]);
}
