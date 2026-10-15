import { render, screen, waitFor } from "@testing-library/react";
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

function blockedReleaseList(overrides: { id?: string; releaseName?: string; hash?: string | null } = {}) {
  return [
    {
      id: overrides.id ?? "blocked-1",
      releaseKey: "key-1",
      releaseName: overrides.releaseName ?? "Arrival.2016.2160p.UHD.BluRay",
      indexerName: "Nebula",
      mediaType: "movie",
      entityId: "movie-1",
      title: "Arrival",
      reasonCode: "manualFailure",
      reason: "You blocked this release from Deluno.",
      torrentHashOrItemId: overrides.hash ?? null,
      downloadClientId: null,
      downloadClientName: null,
      blockedUtc: "2026-09-27T08:00:00Z",
      state: "refused",
      everyIndexer: true
    }
  ];
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

  it("offers to stop a release found downloading, ticked by default, matched from title history by name", async () => {
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

    // #925: "Stop it, delete it and find another" is a checkbox, ticked by
    // default, not a second button — the fuller action is what happens
    // unless a person opts out of it.
    const stopCheckbox = await screen.findByRole("checkbox", { name: /Stop it, delete it and find another/ });
    expect(stopCheckbox).toBeChecked();

    const stopButton = screen.getByRole("button", { name: "Stop it, delete its files and find another" });
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

  it("matches an Activity dispatch entry by its known hash, and unticking the checkbox never stops the download", async () => {
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
    const stopCheckbox = await screen.findByRole("checkbox", { name: /Stop it, delete it and find another/ });
    expect(stopCheckbox).toBeChecked();

    await userEvent.click(stopCheckbox);
    expect(stopCheckbox).not.toBeChecked();

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

/**
 * Lifting a block (#925): offered from every one of those same places, by
 * the same component switching to "Lift the block" once it finds this exact
 * release already refused. Never starts a search (DESIGN-007 decision 8).
 */
describe("lifting a block from anywhere it can be blocked", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it("shows 'Lift the block' instead, once it finds this release already refused", async () => {
    vi.mocked(fetchJson).mockResolvedValue(blockedReleaseList());

    render(
      <BlockReleaseAction
        releaseName="Arrival.2016.2160p.UHD.BluRay"
        indexerName="Nebula"
        mediaType="movie"
        entityId="movie-1"
        title="Arrival"
      />
    );

    expect(await screen.findByRole("button", { name: 'Lift the block on "Arrival"' })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: 'Block "Arrival"' })).not.toBeInTheDocument();
  });

  it("lifts a block without starting a search, matched by hash", async () => {
    vi.mocked(fetchJson).mockResolvedValue(blockedReleaseList({ id: "blocked-9", hash: "abcdef0123456789" }));
    vi.mocked(authedFetch).mockResolvedValue({ ok: true } as Response);
    const onBlocked = vi.fn();

    render(
      <BlockReleaseAction
        releaseName="Arrival.2016.2160p.UHD.BluRay"
        indexerName="Nebula"
        hash="abcdef0123456789"
        mediaType="movie"
        entityId="movie-1"
        title="Arrival"
        onBlocked={onBlocked}
      />
    );

    const trigger = await screen.findByRole("button", { name: 'Lift the block on "Arrival"' });
    await userEvent.click(trigger);

    const confirm = await screen.findByRole("button", { name: "Lift the block" });
    await userEvent.click(confirm);

    expect(authedFetch).toHaveBeenCalledWith("/api/blocked-releases/blocked-9", expect.objectContaining({ method: "DELETE" }));
    // Never a search: no queue or grab call made alongside the lift.
    expect(authedFetch).not.toHaveBeenCalledWith(expect.stringContaining("/queue/"), expect.anything());
    expect(authedFetch).not.toHaveBeenCalledWith(expect.stringContaining("/grab"), expect.anything());
    expect(toasts.success).toHaveBeenCalled();
    expect(onBlocked).toHaveBeenCalled();
  });

  it("does not offer to lift a release nobody has blocked", async () => {
    vi.mocked(fetchJson).mockResolvedValue([]);

    render(
      <BlockReleaseAction
        releaseName="Arrival.2016.2160p.UHD.BluRay"
        indexerName="Nebula"
        mediaType="movie"
        entityId="movie-1"
        title="Arrival"
      />
    );

    expect(await screen.findByRole("button", { name: 'Block "Arrival"' })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /lift the block/i })).not.toBeInTheDocument();
  });
});

/**
 * When a block cannot be done (coverage register, area search-grab): the person is told in words what went wrong, never a bare
 * "failed", and the dialog stays so they can try again or cancel.
 */
describe("a block that cannot be done", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  const props = { releaseName: "Arrival.2016.2160p.UHD.BluRay", indexerName: "Nebula", mediaType: "movie", entityId: "movie-1", title: "Arrival" } as const;

  it("a block the server refuses is explained and the dialog stays open", async () => {
    vi.mocked(fetchJson).mockResolvedValue(emptyOverview());
    vi.mocked(authedFetch).mockResolvedValue({ ok: false, status: 400, json: async () => ({}), clone() { return this; } } as unknown as Response);
    const onBlocked = vi.fn();
    render(<BlockReleaseAction {...props} onBlocked={onBlocked} />);

    await userEvent.click(screen.getByRole("button", { name: 'Block "Arrival"' }));
    await userEvent.click(await screen.findByRole("button", { name: "Block this release" }));

    expect(toasts.error).toHaveBeenCalled();
    expect(String(toasts.error.mock.calls[0][0])).not.toBe("failed");
    expect(toasts.success).not.toHaveBeenCalled();
    expect(onBlocked).not.toHaveBeenCalled();
    expect(screen.getByRole("button", { name: "Block this release" })).toBeInTheDocument();
  });

  it("a stop-and-block the client refuses is explained and nothing is reported as blocked", async () => {
    vi.mocked(fetchJson).mockResolvedValue(overviewWithQueueItem());
    vi.mocked(authedFetch).mockResolvedValue({ ok: false, status: 502, json: async () => ({}), clone() { return this; } } as unknown as Response);
    const onBlocked = vi.fn();
    render(<BlockReleaseAction {...props} onBlocked={onBlocked} />);

    await userEvent.click(screen.getByRole("button", { name: 'Block "Arrival"' }));
    await userEvent.click(await screen.findByRole("button", { name: "Stop it, delete its files and find another" }));

    expect(toasts.error).toHaveBeenCalled();
    expect(toasts.success).not.toHaveBeenCalled();
    expect(onBlocked).not.toHaveBeenCalled();
  });

  it("a lift the server refuses is explained", async () => {
    vi.mocked(fetchJson).mockResolvedValue(blockedReleaseList({ id: "blocked-9", hash: "abcdef0123456789" }));
    vi.mocked(authedFetch).mockResolvedValue({ ok: false, status: 500, json: async () => ({}), clone() { return this; } } as unknown as Response);
    const onBlocked = vi.fn();
    render(<BlockReleaseAction {...props} hash="abcdef0123456789" onBlocked={onBlocked} />);

    await userEvent.click(await screen.findByRole("button", { name: 'Lift the block on "Arrival"' }));
    await userEvent.click(await screen.findByRole("button", { name: "Lift the block" }));

    expect(toasts.error).toHaveBeenCalled();
    expect(onBlocked).not.toHaveBeenCalled();
  });

  it("after a block, the toast names the release and says every indexer's copy is refused, and the dialog closes", async () => {
    vi.mocked(fetchJson).mockResolvedValue(emptyOverview());
    vi.mocked(authedFetch).mockResolvedValue({ ok: true, json: async () => ({ id: "block-1" }) } as Response);
    render(<BlockReleaseAction {...props} />);

    await userEvent.click(screen.getByRole("button", { name: 'Block "Arrival"' }));
    await userEvent.click(await screen.findByRole("button", { name: "Block this release" }));

    expect(toasts.success).toHaveBeenCalledWith(`"Arrival" blocked; every indexer's copy is refused from now on.`);
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
  });
});
