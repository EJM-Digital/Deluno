using Deluno.Downloader.Engine;
using Deluno.Downloader.Persistence;

namespace Deluno.Downloader.Tests.Persistence;

public class JobHistoryDedupeKeyTests
{
    [Fact]
    public void Nzb_key_is_stable_for_same_name_and_size()
    {
        var a = MakeJob("Movie.2024.1080p.x264-RLSGRP", DownloadProtocol.Nzb, totalBytes: 8_000_000_000);
        var b = MakeJob("Movie.2024.1080p.x264-RLSGRP", DownloadProtocol.Nzb, totalBytes: 8_000_000_000);

        var keyA = JobHistoryDedupeKey.Compute(a);
        var keyB = JobHistoryDedupeKey.Compute(b);

        Assert.NotNull(keyA);
        Assert.Equal(keyA, keyB);
        Assert.StartsWith("nzb:", keyA);
    }

    [Fact]
    public void Nzb_key_distinguishes_different_qualities_by_size()
    {
        // Same display name, different total bytes — different release.
        var p1080 = MakeJob("Movie.2024-RLSGRP", DownloadProtocol.Nzb, totalBytes: 8_000_000_000);
        var p2160 = MakeJob("Movie.2024-RLSGRP", DownloadProtocol.Nzb, totalBytes: 40_000_000_000);

        Assert.NotEqual(JobHistoryDedupeKey.Compute(p1080), JobHistoryDedupeKey.Compute(p2160));
    }

    [Fact]
    public void Nzb_key_distinguishes_different_releases_by_name()
    {
        var ep1 = MakeJob("Show.S01E01-GROUP", DownloadProtocol.Nzb, totalBytes: 1_500_000_000);
        var ep2 = MakeJob("Show.S01E02-GROUP", DownloadProtocol.Nzb, totalBytes: 1_500_000_000);

        Assert.NotEqual(JobHistoryDedupeKey.Compute(ep1), JobHistoryDedupeKey.Compute(ep2));
    }

    [Fact]
    public void Torrent_key_uses_v1_infohash_when_available()
    {
        var job = MakeJob("doesnt-matter", DownloadProtocol.Torrent, totalBytes: 0);
        var v1 = "0123456789abcdef0123456789abcdef01234567"; // 40 hex chars

        var key = JobHistoryDedupeKey.Compute(job, torrentInfohashV1Hex: v1);

        Assert.Equal("torrent:" + v1, key);
    }

    [Fact]
    public void Torrent_key_falls_back_to_v2_btmh_when_v1_missing()
    {
        var job = MakeJob("v2-only", DownloadProtocol.Torrent, totalBytes: 0);
        var v2 = "1220ABCDEF1234567890ABCDEF1234567890ABCDEF1234567890ABCDEF12345678"; // sha256-style

        var key = JobHistoryDedupeKey.Compute(job, torrentInfohashV1Hex: null, torrentInfohashV2Hex: v2);

        Assert.Equal("torrent:btmh:" + v2.ToLowerInvariant(), key);
    }

    [Fact]
    public void Torrent_key_is_null_when_no_infohash_resolved()
    {
        var job = MakeJob("failed-before-metadata", DownloadProtocol.Torrent, totalBytes: 0);

        Assert.Null(JobHistoryDedupeKey.Compute(job));
    }

    [Fact]
    public void Torrent_v1_key_is_lowercased()
    {
        var job = MakeJob("anything", DownloadProtocol.Torrent, totalBytes: 0);
        var mixedCase = "0123456789ABCDEF0123456789abcdef01234567";

        var key = JobHistoryDedupeKey.Compute(job, torrentInfohashV1Hex: mixedCase);

        Assert.Equal("torrent:" + mixedCase.ToLowerInvariant(), key);
    }

    [Fact]
    public void Nzb_key_is_case_sensitive_in_display_name()
    {
        // Scene release naming distinguishes by case (rare but spec-legal).
        var upper = MakeJob("RELEASE.NAME", DownloadProtocol.Nzb, totalBytes: 100);
        var lower = MakeJob("release.name", DownloadProtocol.Nzb, totalBytes: 100);

        Assert.NotEqual(JobHistoryDedupeKey.Compute(upper), JobHistoryDedupeKey.Compute(lower));
    }

    private static JobRecord MakeJob(string displayName, DownloadProtocol protocol, long totalBytes)
        => new(
            Id: Guid.NewGuid().ToString("N"),
            Protocol: protocol,
            DisplayName: displayName,
            SourcePath: "",
            SourceKind: protocol == DownloadProtocol.Nzb ? "nzb" : "torrent_file",
            Category: null,
            Priority: 0,
            State: JobLifecycleState.Done,
            StateReason: null,
            Paused: false,
            PasswordProtected: null,
            DownloadDir: "",
            OutputDir: null,
            TotalBytes: totalBytes,
            DownloadedBytes: totalBytes,
            UploadedBytes: 0,
            DispatchId: null,
            LibraryId: null,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            CompletedAt: DateTimeOffset.UtcNow);
}
