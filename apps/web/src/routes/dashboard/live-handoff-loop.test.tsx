import { QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { queryClient } from "../../lib/route-cache";
import { fullSummary } from "./downloads-fixtures";
import { LiveTab } from "./live-tab";

vi.mock("../../lib/use-auth", () => ({ authedFetch: vi.fn() }));

const lists: Record<string, unknown> = {};
vi.mock("../../lib/api", async (importOriginal) => {
  const original = await importOriginal<typeof import("../../lib/api")>();
  const read = async (path: string) => {
    if (path === "/api/settings") return original.emptyPlatformSettingsSnapshot;
    if (path === "/api/setup/progress") return { lastCompletedStep: 9, isSkipped: false, isCompleted: true, updatedUtc: "2026-09-30T00:00:00Z" };
    const key = Object.keys(lists).find((prefix) => path.startsWith(prefix));
    return key ? lists[key] : [];
  };
  return { ...original, fetchJson: read, fetchAllPages: read, fetchPageItems: read };
});

const ago = (ms: number) => new Date(Date.now() - ms).toISOString();

// The real run (CI run 41247553280, outage.inflight): the client lists a film under its torrent's name, and the sent record, which
// the hand-off also carries, names the same file with its extension.
const TORRENT = "E2eOutage.2020.1080p.WEB-DL.x264-E2E";
const SENT = `${TORRENT}.mkv`;

function seed(queueStatus: string, handoffStatus: string | null) {
  lists["/api/download-clients/telemetry"] = {
    capturedUtc: ago(2_000),
    summary: {},
    clients: [{ clientId: "c1", clientName: "qBittorrent", queue: [{ id: "abc123", clientId: "c1", clientName: "qBittorrent", title: TORRENT, releaseName: TORRENT, status: queueStatus, progress: queueStatus === "downloading" ? 40 : 100, speedMbps: queueStatus === "downloading" ? 3 : 0, etaSeconds: 30, errorMessage: null }] }]
  };
  lists["/api/download-dispatches"] = [{ id: "d1", libraryId: "l1", mediaType: "movies", entityType: "movie", entityId: "m1", releaseName: SENT, indexerName: "Fixture", downloadClientId: "c1", downloadClientName: "qBittorrent", status: "sent", notesJson: null, createdUtc: ago(35_000), grabStatus: "succeeded", torrentHashOrItemId: "abc123" }];
  lists["/api/integrations/processors/handoffs"] = handoffStatus ? [{
    id: "h1", libraryId: "l1", mediaType: "movies", clientId: "c1", queueItemId: "abc123", releaseName: SENT, sourcePath: "/d/x", processorName: "Weir", status: handoffStatus,
    outputPath: null, importJobId: null, failureMessage: null, createdUtc: ago(30_000), updatedUtc: ago(5_000),
    processor: { state: "queued", message: "Waiting its turn", queuePosition: 2, scheduledForUtc: null, lastChangedUtc: ago(4_000), checkedUtc: ago(1_000), unreachableSinceUtc: null, answeringSinceUtc: null }
  }] : [];
  lists["/api/integrations/processors/connections"] = [{ id: "p1", name: "Weir", isEnabled: true, healthStatus: "healthy", lastHealthMessage: "", lastHealthTestUtc: ago(10_000) }];
}

describe("the Live board while one film is being fetched under two names (#1459)", () => {
  beforeEach(() => {
    queryClient.clear();
    for (const key of Object.keys(lists)) delete lists[key];
  });

  for (const [queue, handoff] of [["downloading", null], ["waitingForProcessor", "started"], ["completed", "submitted"]] as const) {
    it(`draws one card and no render loop (client item ${queue}, hand-off ${handoff ?? "none"})`, async () => {
      seed(queue, handoff);
      const errors: string[] = [];
      const spy = vi.spyOn(console, "error").mockImplementation((...args) => { errors.push(args.map(String).join(" ")); });
      const { container } = render(
        <QueryClientProvider client={queryClient}>
          <MemoryRouter>
            <LiveTab summary={fullSummary} />
          </MemoryRouter>
        </QueryClientProvider>
      );
      expect((await screen.findAllByText(/E2eOutage/)).length).toBeGreaterThan(0);
      await new Promise((resolve) => setTimeout(resolve, 600));
      spy.mockRestore();
      expect(errors.join("\n")).not.toMatch(/Maximum update depth|same key/);
      // One film, one card.
      expect(container.querySelectorAll("[data-card-poster]").length).toBe(1);
    });
  }
});
