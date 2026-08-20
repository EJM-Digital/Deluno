import { describe, expect, it } from "vitest";
import type { DownloadClientItem, DownloadDispatchItem, DownloadTelemetryOverview, IndexerItem, MovieListItem, MovieWantedSummary, SeriesListItem, SeriesWantedSummary } from "./api";
import { adaptActiveDownloads, adaptIndexerHealth, adaptMovieItems, adaptSeriesItems, adaptTelemetryDownloads } from "./ui-adapters";

describe("UI adapters", () => {
  it("adapts representative movie and series data and accepts empty lists", () => {
    const movie = { id: "movie-1", title: "Arrival", releaseYear: 2016, posterUrl: null, backdropUrl: null, currentQuality: "WEB 1080p", hasFile: true, monitored: true, fileSizeBytes: 1024 ** 3, rating: 8, ratings: [], genres: "Drama, Science Fiction", createdUtc: "2024-01-01T00:00:00Z", overview: null, metadataJson: '{"codec":"H.265","keywords":"alien, language"}' } as unknown as MovieListItem;
    const series = { id: "series-1", title: "The Expanse", startYear: 2015, posterUrl: null, backdropUrl: null, currentQuality: null, hasFile: false, monitored: false, fileSizeBytes: null, rating: 8.5, ratings: [], genres: "Drama, Science Fiction", createdUtc: "2024-01-01T00:00:00Z", overview: null, metadataJson: "{}" } as unknown as SeriesListItem;
    const movieWanted = { recentItems: [{ movieId: "movie-1", wantedStatus: "upgrade", wantedReason: "Quality upgrade", currentQuality: "WEB 1080p", targetQuality: "Bluray 2160p" }] } as MovieWantedSummary;
    const seriesWanted = { recentItems: [] } as unknown as SeriesWantedSummary;

    expect(adaptMovieItems([movie], movieWanted)[0]).toMatchObject({ id: "movie-1", type: "movie", status: "downloaded", codec: "H.265", keywords: ["alien", "language"], currentQuality: "WEB 1080p", targetQuality: "Bluray 2160p" });
    expect(adaptSeriesItems([series], seriesWanted)[0]).toMatchObject({ id: "series-1", type: "show", status: "missing", monitored: false });
    expect(adaptMovieItems([], movieWanted)).toEqual([]);
    expect(adaptSeriesItems([], seriesWanted)).toEqual([]);
  });

  it("adapts active and telemetry downloads while respecting their visible limits", () => {
    const dispatch = { id: "dispatch-1", releaseName: "Arrival.2016.1080p", indexerName: "Example" } as DownloadDispatchItem;
    const telemetry = { clients: [{ queue: [{ id: "queue-1", title: "Arrival", releaseName: "Arrival.2016", category: "movies", protocol: "qbittorrent", progress: 42.3, speedMbps: 12.4, etaSeconds: 61, peers: 8, indexerName: "Example", clientName: "qBittorrent", status: "downloading", addedUtc: "2024-01-01T00:00:00Z" }] }] } as DownloadTelemetryOverview;

    expect(adaptActiveDownloads([dispatch])[0]).toMatchObject({ id: "dispatch-1", title: "Arrival.2016.1080p", indexer: "Example" });
    expect(adaptTelemetryDownloads(telemetry)[0]).toMatchObject({ id: "queue-1", title: "Arrival", progress: 42, etaMinutes: 2, indexer: "Example -> qBittorrent" });
    expect(adaptActiveDownloads([])).toEqual([]);
    expect(adaptTelemetryDownloads({ clients: [] } as unknown as DownloadTelemetryOverview)).toEqual([]);
  });

  it("combines indexer and download-client health and handles no connections", () => {
    const indexer = { id: "indexer-1", name: "Indexer", healthStatus: "healthy", lastHealthLatencyMs: 120 } as IndexerItem;
    const client = { id: "client-1", name: "Client", healthStatus: "untested", lastHealthLatencyMs: null } as DownloadClientItem;
    expect(adaptIndexerHealth([indexer], [client])).toEqual([
      { id: "indexer-1", name: "Indexer", status: "healthy", responseMs: 120 },
      { id: "client-1", name: "Client", status: "degraded", responseMs: null }
    ]);
    expect(adaptIndexerHealth([], [])).toEqual([]);
  });
});
