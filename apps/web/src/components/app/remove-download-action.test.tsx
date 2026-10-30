import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { DownloadTelemetryOverview } from "../../lib/api";
import { authedFetch } from "../../lib/use-auth";
import { fetchJson } from "../../lib/api/client";
import { RemoveDownloadAction } from "./remove-download-action";

vi.mock("../../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("../../lib/api/client", async (importOriginal) => ({ ...(await importOriginal<typeof import("../../lib/api/client")>()), fetchJson: vi.fn() }));

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

function overviewWithQueueItem(): DownloadTelemetryOverview {
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
            id: "abcdef0123456789",
            clientId: "client-1",
            clientName: "qBittorrent",
            protocol: "qbittorrent",
            mediaType: "movie",
            title: "Arrival",
            releaseName: "Arrival.2016.2160p.UHD.BluRay",
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
 * The Queue's "why remove this" dialog, offered from a title's dispatch and
 * search history (#924). Only offered while the download is still in a
 * client — matched exactly the way block-release.ts matches its own "stop
 * it" option, by hash first and then by release name.
 */
describe("removing a download from a title's history", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it("offers nothing for a release the client is no longer holding", async () => {
    vi.mocked(fetchJson).mockResolvedValue(emptyOverview());

    const { container } = render(
      <RemoveDownloadAction
        releaseName="Arrival.2016.2160p.UHD.BluRay"
        indexerName="Nebula"
        mediaType="movie"
        entityId="movie-1"
        title="Arrival"
      />
    );

    await vi.waitFor(() => expect(fetchJson).toHaveBeenCalled());
    expect(container).toBeEmptyDOMElement();
  });

  it("offers the three-way removal choice once it finds the release still in a client", async () => {
    vi.mocked(fetchJson).mockResolvedValue(overviewWithQueueItem());
    vi.mocked(authedFetch).mockResolvedValue({
      ok: true,
      json: async () => ({
        clientId: "client-1",
        queueItemId: "abcdef0123456789",
        reason: "failed",
        succeeded: true,
        message: 'You marked "Arrival" as bad: removed from qBittorrent, deleted its files, blocklisted the release, and started a new search.',
        blocklisted: true,
        searchStarted: true,
        entityId: "movie-1",
        mediaType: "movie"
      })
    } as Response);
    const onRemoved = vi.fn();

    render(
      <RemoveDownloadAction
        releaseName="Arrival.2016.2160p.UHD.BluRay"
        indexerName="Nebula"
        mediaType="movie"
        entityId="movie-1"
        title="Arrival"
        onRemoved={onRemoved}
      />
    );

    const trigger = await screen.findByRole("button", { name: 'Remove "Arrival" from qBittorrent' });
    await userEvent.click(trigger);

    expect(await screen.findByRole("button", { name: /It's bad/ })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /I don't want it/ })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Just stop tracking it/ })).toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: /It's bad/ }));

    expect(authedFetch).toHaveBeenCalledWith(
      "/api/download-clients/client-1/queue/abcdef0123456789/remove",
      expect.objectContaining({
        method: "POST",
        body: JSON.stringify({ reason: "failed" })
      })
    );
    expect(toasts.success).toHaveBeenCalled();
    expect(onRemoved).toHaveBeenCalled();
  });

  async function openDialogFor(props: { title?: string } = { title: "Arrival" }) {
    vi.mocked(fetchJson).mockResolvedValue(overviewWithQueueItem());
    render(
      <RemoveDownloadAction
        releaseName="Arrival.2016.2160p.UHD.BluRay"
        indexerName="Nebula"
        mediaType="movie"
        entityId="movie-1"
        title={props.title ?? ""}
      />
    );
    return screen.findByRole("button", { name: /^Remove ".*" from qBittorrent$/ });
  }

  it("names the release itself when the title is blank", async () => {
    const trigger = await openDialogFor({ title: "" });
    expect(trigger).toHaveAccessibleName('Remove "Arrival.2016.2160p.UHD.BluRay" from qBittorrent');
  });

  it("tells the person the server's reason when it refuses, and keeps the dialog open", async () => {
    vi.mocked(authedFetch).mockResolvedValue({
      ok: false,
      json: async () => ({ succeeded: false, message: "qBittorrent would not let go of it." })
    } as Response);
    await userEvent.click(await openDialogFor());
    await userEvent.click(await screen.findByRole("button", { name: /It's bad/ }));
    await vi.waitFor(() => expect(toasts.error).toHaveBeenCalledWith("qBittorrent would not let go of it."));
    expect(toasts.success).not.toHaveBeenCalled();
    expect(screen.getByRole("button", { name: /It's bad/ })).toBeInTheDocument();
  });

  it("says a plain sentence when the server gave no reason at all", async () => {
    vi.mocked(authedFetch).mockResolvedValue({
      ok: false,
      json: async () => { throw new Error("no body"); },
      text: async () => ""
    } as unknown as Response);
    await userEvent.click(await openDialogFor());
    await userEvent.click(await screen.findByRole("button", { name: /Just stop tracking it/ }));
    await vi.waitFor(() => expect(toasts.error).toHaveBeenCalledWith("That removal could not be completed."));
  });

  it("says the same plain sentence when the request fails with something that is not an error", async () => {
    vi.mocked(authedFetch).mockRejectedValue("offline");
    await userEvent.click(await openDialogFor());
    await userEvent.click(await screen.findByRole("button", { name: /I don't want it/ }));
    await vi.waitFor(() => expect(toasts.error).toHaveBeenCalledWith("That removal could not be completed."));
  });

  it("ignores a queue answer that arrives after the button has gone", async () => {
    let finish: (value: DownloadTelemetryOverview) => void = () => undefined;
    vi.mocked(fetchJson).mockReturnValue(new Promise((resolve) => { finish = resolve as typeof finish; }));
    const { unmount, container } = render(
      <RemoveDownloadAction releaseName="Arrival.2016.2160p.UHD.BluRay" indexerName="Nebula" mediaType="movie" entityId="movie-1" title="Arrival" />
    );
    unmount();
    finish(overviewWithQueueItem());
    await Promise.resolve();
    expect(container).toBeEmptyDOMElement();
  });
});
