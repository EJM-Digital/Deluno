import { describe, expect, it } from "vitest";
import type { DownloadClientItem, DownloadDispatchItem, DownloadTelemetryOverview, IndexerItem, MovieListItem, SeriesListItem } from "./api";
import { adaptActiveDownloads, adaptIndexerHealth, adaptMovieItems, adaptSeriesItems, adaptTelemetryDownloads } from "./ui-adapters";

describe("UI adapters", () => {
  it("adapts representative movie and series data and accepts empty lists", () => {
    const movie = { id: "movie-1", title: "Arrival", releaseYear: 2016, posterUrl: null, backdropUrl: null, currentQuality: "WEB 1080p", targetQuality: "Bluray 2160p", wantedStatus: "upgrade", wantedReason: "Quality upgrade", hasFile: true, monitored: true, fileSizeBytes: 1024 ** 3, rating: 8, ratings: [], genres: "Drama, Science Fiction", createdUtc: "2024-01-01T00:00:00Z", overview: null, metadataJson: '{"codec":"H.265","keywords":"alien, language"}' } as unknown as MovieListItem;
    const series = { id: "series-1", title: "The Expanse", startYear: 2015, posterUrl: null, backdropUrl: null, currentQuality: null, hasFile: false, monitored: false, fileSizeBytes: null, rating: 8.5, ratings: [], genres: "Drama, Science Fiction", createdUtc: "2024-01-01T00:00:00Z", overview: null, metadataJson: "{}" } as unknown as SeriesListItem;

    expect(adaptMovieItems([movie])[0]).toMatchObject({ id: "movie-1", type: "movie", codec: "H.265", keywords: ["alien", "language"], currentQuality: "WEB 1080p", targetQuality: "Bluray 2160p" });
    expect(adaptSeriesItems([series])[0]).toMatchObject({ id: "series-1", type: "show", monitored: false });
    expect(adaptMovieItems([])).toEqual([]);
    expect(adaptSeriesItems([])).toEqual([]);
  });

  /**
   * The search state has to come off the page item, because the summary it used
   * to come from — `/api/movies/wanted` — returns at most 25 `recentItems`. Any
   * title past the 25th had no entry in that map, so its card silently lost its
   * status, its reason and its target quality and fell back to "is there a
   * file". Eleven movies on a lab rig all fit inside 25; twenty thousand do not.
   */
  it("reads every title's search state from the title, however deep the page", () => {
    const items = Array.from({ length: 400 }, (_, index) => ({
      id: `movie-${index}`,
      title: `Title ${index}`,
      releaseYear: 2016,
      posterUrl: null,
      backdropUrl: null,
      hasFile: true,
      monitored: true,
      currentQuality: "WEB 1080p",
      targetQuality: "Bluray 2160p",
      wantedStatus: "upgrade",
      wantedReason: "Better copy available",
      libraryId: "library-movies",
      rating: null,
      ratings: [],
      genres: "",
      createdUtc: "2024-01-01T00:00:00Z",
      overview: null,
      metadataJson: "{}"
    })) as unknown as MovieListItem[];

    const adapted = adaptMovieItems(items);

    expect(adapted).toHaveLength(400);
    expect(adapted.every((item) => item.releaseStatus === "Upgradable")).toBe(true);
    expect(adapted.every((item) => item.wantedReason === "Better copy available")).toBe(true);
    expect(adapted.every((item) => item.libraryId === "library-movies")).toBe(true);
    expect(adapted.every((item) => item.targetQuality === "Bluray 2160p")).toBe(true);
  });

  /**
   * A title has no availability chip any more.
   *
   * `MediaItem.status` was `hasFile ? "downloaded" : "missing"` and nothing
   * else — a movie below its target looked identical to a finished one, and its
   * colour table painted the missing case amber, the one signal reserved for
   * "a person is needed" (#302). The mark reads the wanted status instead, so
   * what is left to check here is the Release status *filter*, which is still a
   * text facet and must speak the same words the mark does.
   */
  describe("the release status facet", () => {
    const movie = (overrides: Record<string, unknown>) =>
      ({ id: "movie-1", title: "Arrival", releaseYear: 2016, posterUrl: null, backdropUrl: null, currentQuality: "WEB 2160p", hasFile: true, monitored: true, fileSizeBytes: 1024 ** 3, rating: 8, ratings: [], genres: "", createdUtc: "2024-01-01T00:00:00Z", overview: null, metadataJson: "{}", ...overrides }) as unknown as MovieListItem;

    it("never offers Downloading, which the catalogue cannot know about", () => {
      // The half #299 left behind: the filter answered "Downloading" for
      // `waiting`, which the server sets on a movie that has a file and meets its
      // target (#300) — so filtering for Downloading returned the finished ones.
      // This adapter is fed the catalogue, which carries no live transfer state.
      for (const hasFile of [true, false]) {
        for (const wantedStatus of ["covered", "upgrade", "missing", "upcoming"]) {
          expect(adaptMovieItems([movie({ hasFile, wantedStatus })])[0].releaseStatus).not.toBe("Downloading");
        }
      }
    });

    it("gives the release status the same words the mark shows", () => {
      expect(adaptMovieItems([movie({ wantedStatus: "covered" })])[0].releaseStatus).toBe("Quality met");
      expect(adaptMovieItems([movie({ wantedStatus: "upgrade" })])[0].releaseStatus).toBe("Upgradable");
      expect(adaptMovieItems([movie({ hasFile: false, wantedStatus: "missing" })])[0].releaseStatus).toBe("Missing");
      expect(adaptMovieItems([movie({ wantedStatus: "upcoming" })])[0].releaseStatus).toBe("Upcoming");
    });

    it("claims only what it knows when a title has no wanted record", () => {
      expect(adaptMovieItems([movie({})])[0].releaseStatus).toBe("On disk");
      expect(adaptMovieItems([movie({ hasFile: false })])[0].releaseStatus).toBe("Missing");
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
