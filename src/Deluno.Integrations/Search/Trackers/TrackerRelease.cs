namespace Deluno.Integrations.Search.Trackers;

/// <summary>One release as a tracker listed it (#606), before Deluno judges it.</summary>
public sealed class TrackerRelease
{
    public string? Title { get; set; }
    public string? Description { get; set; }

    /// <summary>A .torrent address, or a page the download block finds the link on.</summary>
    public string? DownloadUrl { get; set; }
    public string? MagnetUrl { get; set; }
    public string? InfoHash { get; set; }
    public string? DetailsUrl { get; set; }
    public string? PosterUrl { get; set; }
    public List<int> Categories { get; } = [];
    public long? Size { get; set; }
    public int? Seeders { get; set; }
    public int? Peers { get; set; }
    public int? Grabs { get; set; }
    public int? Files { get; set; }
    public DateTimeOffset? PublishDate { get; set; }
    public double? DownloadVolumeFactor { get; set; }
    public double? UploadVolumeFactor { get; set; }
    public double? MinimumRatio { get; set; }
    public long? MinimumSeedTimeSeconds { get; set; }
    public string? ImdbId { get; set; }
    public int? TmdbId { get; set; }
    public int? TvdbId { get; set; }
    public int? Year { get; set; }
    public string? Genre { get; set; }

    /// <summary>Where Deluno fetches the release from: the download address, else the magnet.</summary>
    public string? Link => DownloadUrl ?? MagnetUrl;
}

/// <summary>A search's outcome: its releases, and each page that could not be read and why.</summary>
public sealed record TrackerSearchResult(IReadOnlyList<TrackerRelease> Releases, IReadOnlyList<string> Problems, int RowsSkipped);
