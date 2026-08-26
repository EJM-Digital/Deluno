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

  /**
   * The availability chip answers one question: does Deluno have the file.
   * It used to read the wanted status first and show "Downloading" for anything
   * `waiting` — which the server sets on a film that *has* a file and already
   * meets or beats its target quality. So an imported, verified film displayed
   * on its card as still coming down.
   */
  describe("the availability chip", () => {
    const movie = (overrides: Record<string, unknown>) =>
      ({ id: "movie-1", title: "Arrival", releaseYear: 2016, posterUrl: null, backdropUrl: null, currentQuality: "WEB 2160p", hasFile: true, monitored: true, fileSizeBytes: 1024 ** 3, rating: 8, ratings: [], genres: "", createdUtc: "2024-01-01T00:00:00Z", overview: null, metadataJson: "{}", ...overrides }) as unknown as MovieListItem;
    const wanted = (wantedStatus: string) =>
      ({ recentItems: [{ movieId: "movie-1", wantedStatus, wantedReason: "", currentQuality: "WEB 2160p", targetQuality: "WEB 1080p" }] }) as MovieWantedSummary;

    it("says downloaded for a film on disk, whatever it is waiting for", () => {
      for (const status of ["waiting", "covered", "upgrade", "missing"]) {
        expect(adaptMovieItems([movie({})], wanted(status))[0].status).toBe("downloaded");
      }
    });

    it("says missing for a film with no file, whatever it is waiting for", () => {
      for (const status of ["waiting", "covered", "upgrade", "missing"]) {
        expect(adaptMovieItems([movie({ hasFile: false })], wanted(status))[0].status).toBe("missing");
      }
    });

    it("never claims a catalogue item is downloading", () => {
      // This adapter is fed the catalogue, which carries no live transfer
      // state. Progress on a card needs telemetry wired in, not a wanted
      // status pressed into service as a stand-in.
      for (const hasFile of [true, false]) {
        for (const status of ["waiting", "covered", "upgrade", "missing"]) {
          expect(adaptMovieItems([movie({ hasFile })], wanted(status))[0].status).not.toBe("downloading");
        }
      }
    });

    it("applies the same rule to shows", () => {
      const show = (hasFile: boolean) =>
        ({ id: "series-1", title: "Severance", startYear: 2022, posterUrl: null, backdropUrl: null, currentQuality: null, hasFile, monitored: true, fileSizeBytes: null, rating: 8, ratings: [], genres: "", createdUtc: "2024-01-01T00:00:00Z", overview: null, metadataJson: "{}" }) as unknown as SeriesListItem;
      const seriesWanted = { recentItems: [{ seriesId: "series-1", wantedStatus: "waiting", wantedReason: "" }] } as unknown as SeriesWantedSummary;

      expect(adaptSeriesItems([show(true)], seriesWanted)[0].status).toBe("downloaded");
      expect(adaptSeriesItems([show(false)], seriesWanted)[0].status).toBe("missing");
    });
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
