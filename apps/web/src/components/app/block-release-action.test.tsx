import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { DownloadTelemetryOverview } from "../../lib/api";
import { authedFetch } from "../../lib/use-auth";
import { fetchJson } from "../../lib/api/client";
import { BlockReleaseAction } from "./block-release-action";

vi.mock("../../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("../../lib/api/client", () => ({ fetchJson: vi.fn() }));

vi.mock("../shell/toaster", () => ({
  toast: { success: vi.fn(), error: vi.fn() }
}));
const { toast: toasts } = (await import("../shell/toaster")) as unknown as {
  toast: { success: ReturnType<typeof vi.fn>; error: ReturnType<typeof vi.fn> };
};

function emptyOverview(): DownloadTelemetryOverview {
  return {
    summary: {
      activeCount: 0,
      queuedCount: 0,
      completedCount: 0,
      stalledCount: 0,
      processingCount: 0,
      importReadyCount: 0,
      totalSpeedMbps: 0,
      totalUploadSpeedMbps: 0,
      waitingForProcessorCount: 0
    },
    clients: [],
    capturedUtc: "2026-09-27T09:00:00Z"
  };
}

function overviewWithQueueItem(overrides: { id?: string; releaseName?: string } = {}): DownloadTelemetryOverview {
  return {
    ...emptyOverview(),
    clients: [
      {
        clientId: "client-1",
        clientName: "qBittorrent",
        protocol: "qbittorrent",
        endpointUrl: null,
        healthStatus: "ok",
        lastHealthMessage: null,
        capabilities: {
          supportsQueue: true,
          supportsHistory: true,
          supportsPauseResume: true,
          supportsRemove: true,
          supportsRecheck: true,
          supportsImportPath: true,
          authMode: "none"
        },
        summary: emptyOverview().summary,
        queue: [
          {
            id: overrides.id ?? "abcdef0123456789",
            clientId: "client-1",
            clientName: "qBittorrent",
            protocol: "qbittorrent",
            mediaType: "movie",
            title: "Arrival",
            releaseName: overrides.releaseName ?? "Arrival.2016.2160p.UHD.BluRay",
            category: "movies",
            status: "downloading",
            progress: 0.4,
            speedMbps: 2,
            etaSeconds: 600,
            sizeBytes: 1_000_000,
            downloadedBytes: 400_000,
            peers: 5,
            indexerName: "Nebula",
            errorMessage: null,
            addedUtc: "2026-09-27T08:00:00Z",
            sourcePath: null,
            libraryId: null,
            healthFindings: null
          }
        ],
        history: [],
        capturedUtc: "2026-09-27T09:00:00Z",
        historyTruncated: false
      }
    ]
  };
}

/**
 * Cleanup sheet J5 (#925): "Block this release" appears wherever a release
 * is named — a search result never grabbed, a title's search history, an
 * Activity entry — and only offers to stop an in-flight download when
 * Deluno actually finds one, matched by hash or name against the same
 * telemetry the Queue reads.
 */
describe("blocking a release from anywhere it is named", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it("has a unique, descriptive accessible name for a search result never downloaded", () => {
    render(
      <BlockReleaseAction
        releaseName="Arrival.2016.2160p.UHD.BluRay"
        indexerName="Nebula"
        mediaType="movie"
        entityId="movie-1"
        title="Arrival"
      />
    );

    expect(screen.getByRole("button", { name: 'Block "Arrival"' })).toBeInTheDocument();
  });

  it("blocks a search result with no matching download, without offering to stop anything", async () => {
    vi.mocked(fetchJson).mockResolvedValue(emptyOverview());
    vi.mocked(authedFetch).mockResolvedValue({
      ok: true,
      json: async () => ({ id: "block-1", releaseName: "Arrival.2016.2160p.UHD.BluRay" })
    } as Response);
    const onBlocked = vi.fn();

    render(
      <BlockReleaseAction
        releaseName="Arrival.2016.2160p.UHD.BluRay"
        indexerName="Nebula"
        mediaType="movie"
        entityId="movie-1"
        title="Arrival"
        onBlocked={onBlocked}
      />
    );

    await userEvent.click(screen.getByRole("button", { name: 'Block "Arrival"' }));
    expect(await screen.findByRole("button", { name: "Block this release" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /stop it/i })).not.toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: "Block this release" }));

    expect(authedFetch).toHaveBeenCalledWith(
      "/api/blocked-releases",
      expect.objectContaining({
        method: "POST",
        body: JSON.stringify({
          releaseName: "Arrival.2016.2160p.UHD.BluRay",
          indexerName: "Nebula",
          torrentHashOrItemId: null,
          mediaType: "movie",
          entityId: "movie-1",
          title: "Arrival"
        })
      })
    );
    expect(toasts.success).toHaveBeenCalled();
    expect(onBlocked).toHaveBeenCalled();
  });

  it("offers to stop a release found downloading, matched from title history by name", async () => {
    vi.mocked(fetchJson).mockResolvedValue(overviewWithQueueItem({ releaseName: "Some.Show.S01E01.1080p.WEB-DL" }));
    vi.mocked(authedFetch).mockResolvedValue({ ok: true, json: async () => ({}) } as Response);
    const onBlocked = vi.fn();

    render(
      <BlockReleaseAction
        releaseName="Some.Show.S01E01.1080p.WEB-DL"
        indexerName="Nebula"
        mediaType="tv"
        entityId="series-1"
        title="Some Show"
        onBlocked={onBlocked}
      />
    );

    await userEvent.click(screen.getByRole("button", { name: 'Block "Some Show"' }));

    const stopButton = await screen.findByRole("button", { name: "Stop it, delete its files and find another" });
    expect(screen.getByRole("button", { name: "Just block it" })).toBeInTheDocument();

    await userEvent.click(stopButton);

    expect(authedFetch).toHaveBeenCalledWith(
      "/api/download-clients/client-1/queue/abcdef0123456789/remove",
      expect.objectContaining({
        method: "POST",
        body: JSON.stringify({ reason: "failed" })
      })
    );
    expect(onBlocked).toHaveBeenCalled();
  });

  it("matches an Activity dispatch entry by its known hash, and 'just block it' never stops the download", async () => {
    vi.mocked(fetchJson).mockResolvedValue(overviewWithQueueItem({ id: "ABCDEF0123456789" }));
    vi.mocked(authedFetch).mockResolvedValue({ ok: true, json: async () => ({}) } as Response);
    const onBlocked = vi.fn();

    render(
      <BlockReleaseAction
        releaseName="Arrival.2016.2160p.UHD.BluRay"
        indexerName="Nebula"
        hash="abcdef0123456789"
        mediaType="movie"
        entityId="movie-1"
        onBlocked={onBlocked}
      />
    );

    await userEvent.click(screen.getByRole("button", { name: 'Block "Arrival.2016.2160p.UHD.BluRay"' }));
    await screen.findByRole("button", { name: "Stop it, delete its files and find another" });

    await userEvent.click(screen.getByRole("button", { name: "Just block it" }));

    expect(authedFetch).toHaveBeenCalledWith(
      "/api/blocked-releases",
      expect.objectContaining({ method: "POST" })
    );
    expect(authedFetch).not.toHaveBeenCalledWith(
      expect.stringContaining("/queue/"),
      expect.anything()
    );
    expect(onBlocked).toHaveBeenCalled();
  });
});
