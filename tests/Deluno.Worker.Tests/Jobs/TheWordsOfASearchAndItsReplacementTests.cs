using Deluno.Jobs.Contracts;
using Deluno.Worker.Jobs;

namespace Deluno.Worker.Tests.Jobs;

/// <summary>
/// The sentences a library search says before and after it runs, and how a replacement search (queued because a download or its
/// release went wrong) is told from an ordinary one. Refs #1268.
/// </summary>
public sealed class TheWordsOfASearchAndItsReplacementTests
{
    private static JobQueueItem Job(string? relatedType, string? relatedId)
        => new("job-1", "movies.search", "test", "queued", null, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null, null, null, null, null,
            relatedType, relatedId, null, null, 3, null, null);

    [Fact]
    public void A_search_says_what_it_found_to_look_for_and_what_the_library_still_lacks()
    {
        Assert.Contains("nothing else to look for", SearchExecutionSupport.FormatExecutionMessage("Movies", 0, 2, 1, "movie"), StringComparison.Ordinal);
        Assert.Contains("does not have any indexers linked", SearchExecutionSupport.FormatExecutionMessage("Movies", 1, 0, 1, "movie"), StringComparison.Ordinal);
        Assert.Contains("1 movie to search in Movies, but it still needs a download client", SearchExecutionSupport.FormatExecutionMessage("Movies", 1, 2, 0, "movie"), StringComparison.Ordinal);
        Assert.Contains("3 movies to search in Movies, but it still needs a download client", SearchExecutionSupport.FormatExecutionMessage("Movies", 3, 2, 0, "movie"), StringComparison.Ordinal);
        Assert.Contains("checked 1 movie in Movies using 1 source.", SearchExecutionSupport.FormatExecutionMessage("Movies", 1, 1, 1, "movie"), StringComparison.Ordinal);
        Assert.Contains("checked 2 movies in Movies using 3 sources.", SearchExecutionSupport.FormatExecutionMessage("Movies", 2, 3, 1, "movie"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_finished_search_says_what_it_reviewed_and_what_the_library_still_lacks()
    {
        Assert.Contains("Nothing else needs attention", SearchExecutionSupport.FormatCompletionMessage("Movies", 0, 2, 1, "movie"), StringComparison.Ordinal);
        Assert.Contains("still needs indexers", SearchExecutionSupport.FormatCompletionMessage("Movies", 2, 0, 1, "movie"), StringComparison.Ordinal);
        Assert.Contains("found 1 movie but this library still needs a download client", SearchExecutionSupport.FormatCompletionMessage("Movies", 1, 2, 0, "movie"), StringComparison.Ordinal);
        Assert.Contains("found 2 movies but this library still needs a download client", SearchExecutionSupport.FormatCompletionMessage("Movies", 2, 2, 0, "movie"), StringComparison.Ordinal);
        Assert.Contains("reviewed 1 movie for new or better releases", SearchExecutionSupport.FormatCompletionMessage("Movies", 1, 2, 1, "movie"), StringComparison.Ordinal);
        Assert.Contains("reviewed 4 movies for new or better releases", SearchExecutionSupport.FormatCompletionMessage("Movies", 4, 2, 1, "movie"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("health-replacement", true)]
    [InlineData("recovery-retry", true)]
    [InlineData("grab-rejected", true)]
    [InlineData("external-redownload", true)]
    [InlineData("schedule", false)]
    [InlineData(null, false)]
    public void A_search_queued_because_a_download_went_wrong_is_a_replacement_search(string? triggeredBy, bool replacement)
        => Assert.Equal(replacement, SearchExecutionSupport.IsReplacementSearch(triggeredBy));

    [Fact]
    public void Only_a_search_queued_for_a_dispatch_names_the_download_it_replaces()
    {
        Assert.Equal("dispatch-1", SearchExecutionSupport.ReplacedDispatchId(Job("download_dispatch", "dispatch-1")));
        Assert.Null(SearchExecutionSupport.ReplacedDispatchId(Job("download_dispatch", " ")));
        Assert.Null(SearchExecutionSupport.ReplacedDispatchId(Job("download_dispatch", null)));
        Assert.Null(SearchExecutionSupport.ReplacedDispatchId(Job("movie", "movie-1")));
        Assert.Null(SearchExecutionSupport.ReplacedDispatchId(Job(null, null)));
    }

    [Fact]
    public void A_replacement_search_that_finds_nothing_else_says_so_and_that_it_will_look_again()
    {
        var message = SearchExecutionSupport.NoOtherReleaseMessage("Example Movie");

        Assert.Contains("another copy of Example Movie", message, StringComparison.Ordinal);
        Assert.Contains("look again at the next scheduled search", message, StringComparison.Ordinal);
    }
}
