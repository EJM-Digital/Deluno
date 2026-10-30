using Deluno.Contracts;
using Deluno.Recovery.Services;

namespace Deluno.Persistence.Tests.Services;

/// <summary>
/// The composite that lets a caller outside both media engines stop a title being wanted (#924): each question goes to the one engine
/// that owns the entity type, and an entity type nobody owns is answered "no" or "don't know", never guessed.
/// </summary>
public sealed class CompositeDispatchTitleUnmonitorServiceTests
{
    private sealed class Engine(string owns, bool? hasFile, bool unmonitored) : IDispatchTitleUnmonitorComponent
    {
        public List<string> Calls { get; } = [];

        public bool CanHandle(string entityType) => entityType == owns;

        public Task<bool> UnmonitorAsync(string entityType, string entityId, CancellationToken cancellationToken)
        {
            Calls.Add($"unmonitor:{entityType}:{entityId}");
            return Task.FromResult(unmonitored);
        }

        public Task<bool?> HasFileAsync(string entityType, string entityId, CancellationToken cancellationToken)
        {
            Calls.Add($"hasfile:{entityType}:{entityId}");
            return Task.FromResult(hasFile);
        }
    }

    [Fact]
    public async Task Each_question_goes_to_the_engine_that_owns_the_entity_type_and_to_no_other()
    {
        var movies = new Engine("movie", hasFile: true, unmonitored: true);
        var series = new Engine("episode", hasFile: false, unmonitored: false);
        var composite = new CompositeDispatchTitleUnmonitorService([movies, series]);

        Assert.True(composite.CanHandle("movie"));
        Assert.True(composite.CanHandle("episode"));
        Assert.True(await composite.UnmonitorAsync("movie", "m1", CancellationToken.None));
        Assert.False(await composite.UnmonitorAsync("episode", "e1", CancellationToken.None));
        Assert.True(await composite.HasFileAsync("movie", "m1", CancellationToken.None));
        Assert.False(await composite.HasFileAsync("episode", "e1", CancellationToken.None));

        Assert.Equal(["unmonitor:movie:m1", "hasfile:movie:m1"], movies.Calls);
        Assert.Equal(["unmonitor:episode:e1", "hasfile:episode:e1"], series.Calls);
    }

    [Fact]
    public async Task An_entity_type_no_engine_owns_cannot_be_handled_is_not_unmonitored_and_has_no_known_file()
    {
        var movies = new Engine("movie", hasFile: true, unmonitored: true);
        var composite = new CompositeDispatchTitleUnmonitorService([movies]);

        Assert.False(composite.CanHandle("collection"));
        Assert.False(await composite.UnmonitorAsync("collection", "c1", CancellationToken.None));
        Assert.Null(await composite.HasFileAsync("collection", "c1", CancellationToken.None));
        Assert.Empty(movies.Calls);
    }
}
