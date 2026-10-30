using Deluno.Intake.Contracts;

namespace Deluno.Integrations.Tests.Intake;

/// <summary>The read-only preview of an import list and the approval of what a person picked from it keep exactly what they are given.</summary>
public sealed class IntakeListPreviewContractsTests
{
    [Fact]
    public void A_preview_item_carries_its_action_and_reason_and_defaults_to_no_exclusion()
    {
        var plain = new IntakeListPreviewItem("Dune", 2021, "movie", "tt1160419", "add", "New to your library", "high");
        Assert.Equal(("Dune", 2021, "movie", "tt1160419", "add", "New to your library", "high", null),
            (plain.Title, plain.Year, plain.MediaType, plain.ImdbId, plain.Action, plain.Reason, plain.MatchConfidence, plain.ExclusionId));
        Assert.Equal("x-1", new IntakeListPreviewItem("A", null, "tv", null, "skip", "excluded", "low", "x-1").ExclusionId);
    }

    [Fact]
    public void A_preview_result_lists_what_was_fetched_and_what_is_shown()
    {
        var item = new IntakeListPreviewItem("Dune", 2021, "movie", null, "add", "r", "high");
        var preview = new IntakeListPreviewResult("src-1", "Trending", "tmdb", "movie", null, 120, 100, true, [item], ["Showing the first 100"]);
        Assert.Equal(("src-1", "Trending", "tmdb", "movie", null, 120, 100, true),
            (preview.SourceId, preview.SourceName, preview.Provider, preview.MediaType, preview.TargetLibraryName, preview.FetchedCount, preview.ShownCount, preview.IsTruncated));
        Assert.Same(item, preview.Items[0]);
        Assert.Equal(["Showing the first 100"], preview.Warnings);
    }

    [Fact]
    public void An_approval_carries_the_entries_chosen_and_says_what_came_of_them()
    {
        var entry = new IntakeListEntrySelection("Dune", 2021, "tt1160419");
        var request = new ApproveIntakeListPreviewRequest([entry], true);
        Assert.Same(entry, request.Entries[0]);
        Assert.True(request.SearchAfterAdd);
        Assert.Equal(("Dune", 2021, "tt1160419"), (entry.Title, entry.Year, entry.ImdbId));

        var result = new IntakeListApprovalResult(5, 4, 3, 1, 1, 0, true, "Added 3 titles.");
        Assert.Equal((5, 4, 3, 1, 1, 0, true, "Added 3 titles."),
            (result.SelectedCount, result.MatchedCount, result.AddedCount, result.DuplicateCount, result.SkippedCount, result.ErrorCount, result.SearchRequested, result.Summary));
    }
}
