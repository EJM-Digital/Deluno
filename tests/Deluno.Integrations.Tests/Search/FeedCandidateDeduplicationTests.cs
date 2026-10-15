using Deluno.Integrations.Search;

namespace Deluno.Integrations.Tests.Search;

public sealed class FeedCandidateDeduplicationTests
{
    private static MediaSearchCandidate Candidate(
        string releaseName,
        string quality = "WEB 1080p",
        long? sizeBytes = null,
        string? downloadUrl = null)
        => new(
            ReleaseName: releaseName,
            IndexerId: "indexer-1",
            IndexerName: "Example indexer",
            Quality: quality,
            Score: 10,
            MeetsCutoff: true,
            Summary: "summary",
            DownloadUrl: downloadUrl,
            SizeBytes: sizeBytes);

    [Fact]
    public void The_same_release_named_with_different_case_and_separators_is_kept_once_first_one_wins()
    {
        var kept = FeedMediaSearchPlanner.DeduplicateEquivalentCandidates(
        [
            Candidate("Example.Film.2026.1080p.WEB-DL-GROUP", sizeBytes: 1),
            Candidate("example film 2026 1080p web dl group", sizeBytes: 2),
            Candidate("Example_Film_2026_1080p_WEB_DL_GROUP", sizeBytes: 3)
        ]);

        var only = Assert.Single(kept);
        Assert.Equal(1, only.SizeBytes);
    }

    [Fact]
    public void Two_differently_named_releases_both_stay_selectable()
    {
        var kept = FeedMediaSearchPlanner.DeduplicateEquivalentCandidates(
        [
            Candidate("Example.Film.2026.1080p.WEB-DL-AAA"),
            Candidate("Example.Film.2026.1080p.WEB-DL-BBB")
        ]);

        Assert.Equal(2, kept.Count);
    }

    [Fact]
    public void Releases_with_no_name_are_told_apart_by_quality_size_and_address()
    {
        var kept = FeedMediaSearchPlanner.DeduplicateEquivalentCandidates(
        [
            Candidate(" ", sizeBytes: 100, downloadUrl: "https://indexer.example/get/1"),
            Candidate("", sizeBytes: 100, downloadUrl: " HTTPS://INDEXER.EXAMPLE/get/1 "),
            Candidate("", sizeBytes: 200, downloadUrl: "https://indexer.example/get/1"),
            Candidate("", quality: "Bluray 2160p", sizeBytes: 100, downloadUrl: "https://indexer.example/get/1"),
            Candidate("", sizeBytes: null, downloadUrl: null)
        ]);

        Assert.Equal(4, kept.Count);
        Assert.Equal(100, kept[0].SizeBytes);
        Assert.Equal(200, kept[1].SizeBytes);
    }
}
