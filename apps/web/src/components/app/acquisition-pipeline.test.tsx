import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { DownloadSharingSnapshot, DownloadTelemetrySummary } from "../../lib/api";
import { authedFetch } from "../../lib/use-auth";
import { AcquisitionPipeline } from "./acquisition-pipeline";

vi.mock("../../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("../shell/toaster", () => ({ toast: { success: vi.fn(), error: vi.fn() } }));
const { toast: toasts } = (await import("../shell/toaster")) as unknown as {
  toast: { success: ReturnType<typeof vi.fn>; error: ReturnType<typeof vi.fn> };
};

function summary(): DownloadTelemetrySummary {
  return {
    activeCount: 0,
    queuedCount: 0,
    completedCount: 0,
    stalledCount: 0,
    processingCount: 0,
    importReadyCount: 0,
    totalSpeedMbps: 0,
    totalUploadSpeedMbps: 0,
    waitingForProcessorCount: 0
  };
}

function sharingWithAnUnlinkedHold(): DownloadSharingSnapshot {
  return {
    holds: [
      {
        clientId: "client-1",
        clientName: "qBittorrent",
        queueItemId: "queue-1",
        title: "Big Buck Bunny",
        detail: "No longer shared with your library - the library copy was upgraded or removed.",
        sizeBytes: 58 * 1024 * 1024 * 1024,
        needsYou: false,
        sharesLibraryCopy: false
      }
    ],
    extraBytes: 58 * 1024 * 1024 * 1024,
    driveNote: null,
    observedUtc: "2026-09-27T12:00:00Z"
  };
}

function renderWithClient(sharing: DownloadSharingSnapshot) {
  const client = new QueryClient();
  return render(
    <MemoryRouter>
      <QueryClientProvider client={client}>
        <AcquisitionPipeline summary={summary()} sharing={sharing} />
      </QueryClientProvider>
    </MemoryRouter>
  );
}

/**
 * #926: a seeding download whose library copy was upgraded or deleted is no
 * longer free to share, shows its real size, and can be stopped and deleted
 * from right here rather than only from a torrent client.
 */
describe("a hold whose library copy is gone", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it("shows its size instead of 'shares your copy', and offers Stop & delete", () => {
    renderWithClient(sharingWithAnUnlinkedHold());

    expect(screen.getByText("Big Buck Bunny")).toBeInTheDocument();
    expect(screen.getByText(/no longer shared with your library/i)).toBeInTheDocument();
    expect(screen.getByText("58.0 GB")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Stop seeding and delete "Big Buck Bunny" now/ })).toBeInTheDocument();
  });

  it("removes the download and deletes its files on click", async () => {
    vi.mocked(authedFetch).mockResolvedValue({
      ok: true,
      json: async () => ({
        clientId: "client-1",
        queueItemId: "queue-1",
        reason: "unwanted",
        succeeded: true,
        message: 'Removed "Big Buck Bunny" from qBittorrent and deleted its files.'
      })
    } as Response);

    renderWithClient(sharingWithAnUnlinkedHold());

    await userEvent.click(screen.getByRole("button", { name: /Stop seeding and delete "Big Buck Bunny" now/ }));

    expect(authedFetch).toHaveBeenCalledWith(
      "/api/download-clients/client-1/queue/queue-1/remove",
      expect.objectContaining({ method: "POST", body: JSON.stringify({ reason: "unwanted" }) })
    );
    expect(await screen.findByRole("button", { name: /Stop seeding and delete "Big Buck Bunny" now/ })).toBeInTheDocument();
    expect(toasts.success).toHaveBeenCalled();
  });
});
