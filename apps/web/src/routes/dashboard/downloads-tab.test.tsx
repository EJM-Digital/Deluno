import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClientProvider } from "@tanstack/react-query";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { queryClient } from "../../lib/route-cache";
import { emptySummary, fullSummary } from "./downloads-fixtures";
import { computeAttention } from "./attention";
import { DownloadsTab } from "./downloads-tab";

const authedFetchMock = vi.fn();
vi.mock("../../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetchMock(...args) }));

const lists: Record<string, unknown> = {};
vi.mock("../../lib/api", async (importOriginal) => {
  const original = await importOriginal<typeof import("../../lib/api")>();
  const read = async (path: string) => {
    const key = Object.keys(lists).find((prefix) => path.startsWith(prefix));
    if (!key) throw new Error(`No fixture for ${path}`);
    return lists[key];
  };
  return { ...original, fetchJson: read, fetchAllPages: read, fetchPageItems: read };
});

function seedBusy() {
  lists["/api/download-clients/telemetry"] = {
    capturedUtc: "2026-09-30T10:00:00Z",
    summary: {},
    clients: [{
      clientId: "c1", clientName: "Deluge",
      queue: [
        { id: "q1", clientId: "c1", clientName: "Deluge", title: "Photographer (2026)", releaseName: "Photographer.2026.1080p", status: "downloading", progress: 61, speedMbps: 1.8, etaSeconds: 540, errorMessage: null },
        { id: "q2", clientId: "c1", clientName: "Deluge", title: "The Kid (1921)", releaseName: "The.Kid.1921", status: "stalled", progress: 34, speedMbps: 0, etaSeconds: 0, errorMessage: "No one is sharing it" }
      ]
    }]
  };
  lists["/api/movies/import-recovery"] = { openCount: 1, recentCases: [{ id: "r1", title: "Say Yes to the Dress", failureKind: "importFailed", summary: "Import failed: a replacement is already there.", recommendedAction: "", detailsJson: JSON.stringify({ DispatchId: "d1" }), detectedUtc: "2026-09-30T09:00:00Z" }] };
  lists["/api/series/import-recovery"] = { openCount: 0, recentCases: [] };
  lists["/api/jobs"] = [{ id: "j1", jobType: "filesystem.import.execute", status: "failed", lastError: "Disk was full", attempts: 3, maxAttempts: 3 }];
  lists["/api/integrations/processors/handoffs"] = [{ id: "h1", releaseName: "Metropolis (1927)", status: "failed", failureMessage: "The processor timed out.", processorName: "Cleaner", updatedUtc: "2026-09-30T09:55:00Z" }];
  lists["/api/indexers"] = [
    { id: "i1", name: "althub.co.za", isEnabled: true, healthStatus: "healthy", lastHealthLatencyMs: 492, rateLimitedUntilUtc: null },
    { id: "i2", name: "Nyaa.si", isEnabled: true, healthStatus: "unreachable", lastHealthLatencyMs: null, rateLimitedUntilUtc: null }
  ];
  lists["/api/integrations/processors/connections"] = [{ id: "p1", name: "Cleaner", isEnabled: true, healthStatus: "unreachable", lastHealthMessage: "connection refused", lastHealthTestUtc: "2026-09-30T09:50:00Z" }];
}

function seedEmpty() {
  lists["/api/download-clients/telemetry"] = { capturedUtc: "2026-09-30T10:00:00Z", summary: {}, clients: [] };
  lists["/api/movies/import-recovery"] = { openCount: 0, recentCases: [] };
  lists["/api/series/import-recovery"] = { openCount: 0, recentCases: [] };
  lists["/api/jobs"] = [];
  lists["/api/integrations/processors/handoffs"] = [];
  lists["/api/indexers"] = [];
  lists["/api/integrations/processors/connections"] = [];
}

function renderTab(summary: typeof fullSummary | undefined) {
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>
        <DownloadsTab summary={summary} />
      </MemoryRouter>
    </QueryClientProvider>
  );
}

beforeEach(() => {
  queryClient.clear();
  authedFetchMock.mockReset();
  authedFetchMock.mockResolvedValue({ ok: true, json: async () => ({ message: "Done." }) });
  for (const key of Object.keys(lists)) delete lists[key];
});

describe("Downloads tab", () => {
  it("counts what needs a person, and nothing when all is well", () => {
    // 2 stalled + 2 clients with a button + 1 case + 1 failed import + 1 failed hand-off + 1 indexer + 1 processor
    expect(computeAttention(fullSummary).counts.downloads).toBe(9);
    expect(computeAttention(emptySummary).counts.downloads).toBe(0);
    expect(computeAttention(undefined).counts.downloads).toBe(0);
  });

  it("shows a busy install with each problem's own button, unique names and no raw codes", async () => {
    seedBusy();
    renderTab(fullSummary);

    expect(screen.getByText("Speed now")).toBeInTheDocument();
    expect(screen.getByText("3.9 MB/s")).toBeInTheDocument();
    expect(screen.getByText("48")).toBeInTheDocument();

    expect(await screen.findByText("Photographer (2026)")).toBeInTheDocument();
    expect(screen.getByText("61%")).toBeInTheDocument();
    expect(await screen.findByRole("button", { name: "Search again for The Kid (1921)" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Test SABnzbd again" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Resume NZBGet" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Clear NZBGet’s warnings" })).toBeInTheDocument();
    expect(await screen.findByRole("button", { name: "Test Nyaa.si again" })).toBeInTheDocument();
    expect(await screen.findByRole("button", { name: "Retry Say Yes to the Dress" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Give up on Say Yes to the Dress" })).toBeInTheDocument();
    expect(await screen.findByRole("button", { name: "Retry Metropolis (1927)" })).toBeInTheDocument();
    expect(await screen.findByRole("button", { name: "Test Cleaner again" })).toBeInTheDocument();
    // The processor is named by its own configured name.
    expect(screen.getByRole("heading", { name: "Cleaner" })).toBeInTheDocument();
    expect(screen.queryByText(/weir/i)).toBeNull();

    // Every accessible button name is unique.
    const names = screen.getAllByRole("button").map((button) => button.getAttribute("aria-label") ?? button.textContent);
    expect(new Set(names).size).toBe(names.length);

    const text = document.body.textContent ?? "";
    expect(text).not.toMatch(/undefined|NaN|importFailed|notTransferring|waitingForProcessor/);
  });

  it("links every panel to where you act", async () => {
    seedBusy();
    renderTab(fullSummary);
    await screen.findByText("Photographer (2026)");
    const hrefs = screen.getAllByRole("link").map((link) => link.getAttribute("href"));
    expect(hrefs).toEqual(expect.arrayContaining(["/queue", "/indexers/download-clients", "/indexers/indexers", "/settings/processing", "/search-cycles/blocklist"]));
  });

  it("sends the same requests Transfers does for retry, give up and test again", async () => {
    seedBusy();
    renderTab(fullSummary);
    const user = userEvent.setup();

    await user.click(await screen.findByRole("button", { name: "Retry Say Yes to the Dress" }));
    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/movies/import-recovery/r1/retry", expect.objectContaining({ method: "POST" })));
    await user.click(screen.getByRole("button", { name: "Give up on Say Yes to the Dress" }));
    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/movies/import-recovery/r1/dismiss", expect.objectContaining({ method: "POST" })));
    await user.click(screen.getByRole("button", { name: "Test SABnzbd again" }));
    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/download-clients/c2/test", expect.objectContaining({ method: "POST" })));
    // Resume is the whole-client resume, not the per-item one.
    await user.click(screen.getByRole("button", { name: "Resume NZBGet" }));
    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/download-clients/c3/resume", expect.objectContaining({ method: "POST" })));
    expect(authedFetchMock).not.toHaveBeenCalledWith(expect.stringContaining("/queue/actions"), expect.anything());
  });

  it("says something true on an empty install, with a speed reading and no blank panel", async () => {
    seedEmpty();
    renderTab(emptySummary);

    expect(screen.getByText("0 MB/s")).toBeInTheDocument();
    expect(screen.getByText(/Waiting for the first reading/)).toBeInTheDocument();
    expect(await screen.findByText(/No download client is set up. Add one/)).toBeInTheDocument();
    expect(screen.getByText("Nothing is downloading.")).toBeInTheDocument();
    expect(screen.getByText(/No indexer is set up/)).toBeInTheDocument();
    expect(await screen.findByText(/No processor is set up/)).toBeInTheDocument();
    expect(screen.getByText(/Nothing needs a hand/)).toBeInTheDocument();
    expect(screen.queryAllByRole("button")).toHaveLength(0);
    expect(document.body.textContent ?? "").not.toMatch(/undefined|NaN/);
  });

  it("renders before the summary has arrived without inventing numbers", () => {
    seedEmpty();
    const { container } = renderTab(undefined);
    expect(within(container).getByText("Speed now")).toBeInTheDocument();
    expect(container.textContent).not.toMatch(/undefined|NaN/);
  });
});
