import { QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { queryClient } from "../../lib/route-cache";
import { emptySummary, fullSummary } from "./downloads-fixtures";
import { LiveTab } from "./live-tab";

const authedFetchMock = vi.fn();
vi.mock("../../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetchMock(...args) }));

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
  lists["/api/integrations/processors/handoffs"] = [{ id: "h1", releaseName: "Metropolis (1927)", status: "failed", failureMessage: "The processor timed out.", processorName: "Cleaner", updatedUtc: new Date(Date.now() - 300_000).toISOString() }];
  lists["/api/indexers"] = [{ id: "i2", name: "Nyaa.si", isEnabled: true, healthStatus: "unreachable", lastHealthLatencyMs: null, rateLimitedUntilUtc: null }];
  lists["/api/integrations/processors/connections"] = [{ id: "p1", name: "Cleaner", isEnabled: true, healthStatus: "unreachable", lastHealthMessage: "connection refused", lastHealthTestUtc: "2026-09-30T09:50:00Z" }];
  lists["/api/download-clients/orphans"] = [{ name: "a" }, { name: "b" }];
  lists["/api/series/upcoming"] = [{ seriesId: "s1", title: "Lioness", startYear: 2023, posterUrl: null, episodeId: "e1", seasonNumber: 3, episodeNumber: 2, episodeTitle: "Second", airDateUtc: new Date(Date.now() + 15 * 3_600_000).toISOString() }];
}

function renderTab(summary: typeof fullSummary | undefined) {
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>
        <LiveTab summary={summary} />
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

describe("Live", () => {
  it("has no Needs you and no airing list here: problems are in the bell, the scheduled tasks are on System", () => {
    renderTab(fullSummary);
    expect(screen.queryByRole("region", { name: /needs you/i })).toBeNull();
    expect(screen.queryByRole("region", { name: /airing soon/i })).toBeNull();
    expect(screen.queryByText(/Protected you/)).toBeNull();
    // The top band is three cards of one template, then the journey, the shelf and what was turned down, and the right column.
    for (const name of ["Speed", "Working on now", "Next", "Pipeline", "Your library", "Turned down today", "Sources", "Activity"]) {
      expect(screen.getByRole("region", { name })).toBeInTheDocument();
    }
    expect(screen.queryByRole("region", { name: /^(today|latest)/i })).toBeNull();
  });

  it("lays the tab out on one shared grid, so the rows line up across both columns, with the band first", () => {
    const { container } = renderTab(fullSummary);
    const grid = container.querySelector<HTMLElement>("[data-live-grid='shared']");
    expect(grid).not.toBeNull();
    expect(grid!.style.gridTemplateColumns).toBe("minmax(0, 1fr) clamp(340px, 28%, 600px)");
    expect(grid!.style.gridTemplateAreas).toBe('"now src" "board src" "low act"');
    // The rows are the top band, the journey and the bottom row.
    expect(grid!.style.gridTemplateRows).toMatch(/^\d+px \d+px \d+px$/);
    const cell = (area: string) => Array.from(grid!.children).find((child) => (child as HTMLElement).style.gridArea.replace(/\s/g, "") === area) as HTMLElement;
    expect(within(cell("now")).getByRole("region", { name: "Speed" })).toBeInTheDocument();
    expect(cell("now").firstElementChild?.className).toContain("lb-now");
    expect(within(cell("board")).getByRole("region", { name: "Pipeline" })).toBeInTheDocument();
    // The bottom row is Your library beside Activity; Turned down today is the right column's last row.
    expect(within(cell("low")).getByRole("region", { name: "Your library" })).toBeInTheDocument();
    expect(within(cell("low")).getByRole("region", { name: "Activity" })).toBeInTheDocument();
    expect(within(cell("src")).getByRole("region", { name: "Sources" })).toBeInTheDocument();
    expect(within(cell("act")).getByRole("region", { name: "Turned down today" })).toBeInTheDocument();
  });

  it("shows the journey with a stuck download amber, a Seerr title tagged, the time left, and no button to settle anything", async () => {
    seedBusy();
    lists["/api/dashboard/live-extras"] = { asOfUtc: new Date().toISOString(), sinceUtc: "", arrivals: [], importsToday: 0, importedBytesToday: 0, upgrades: [], protected: { refusedToday: 0, unsafeToday: 0, latest: [] }, retentionReclaimBytes: 0, requests: [{ mediaType: "movie", entityId: "m7", title: "Shogun", status: "added", requestedBy: "Sam", createdUtc: "2026-09-30T00:00:00Z", updatedUtc: "2026-09-30T00:00:00Z" }, { mediaType: "movie", entityId: "m8", title: "Waiting Film", status: "added", requestedBy: "Sam", createdUtc: "2026-09-30T00:00:00Z", updatedUtc: "2026-09-30T00:00:00Z" }], searching: [{ entityType: "movie", entityId: "m7", title: "Shogun", startedBy: "manual", label: "Checking 3 indexers", startedUtc: "2026-09-30T00:00:00Z" }] };
    const summary = { ...fullSummary, activity: { ...fullSummary.activity!, recent: [{ ...fullSummary.activity!.recent[0], relatedEntityType: "movie", relatedEntityId: "m9" }] } };
    renderTab(summary);

    const journey = screen.getByRole("region", { name: "Pipeline" });
    expect(await within(journey).findByText("Photographer (2026)")).toBeInTheDocument();
    expect(within(journey).getByRole("link", { name: /^Photographer \(2026\): 61% · 1.8 MB\/s · Deluge/ })).toBeInTheDocument();
    // The download that has stopped stays on the journey, amber, and says what Deluno is doing about it.
    const stuck = await within(journey).findByRole("link", { name: "The Kid (1921): Stuck at 34% · not moving" });
    expect(stuck).toHaveAttribute("data-journey-card");
    expect(within(journey).queryByRole("button")).toBeNull();
    // Hand-offs, import cases and failed jobs are the bell's, not cards here.
    expect(within(journey).queryByText("An import did not finish")).toBeNull();
    expect(within(journey).queryByText("Say Yes to the Dress")).toBeNull();
    // The title being searched for is the first station, tagged as Seerr's; a requested title only waiting its turn is not on the board (#1442).
    expect(await within(journey).findByRole("link", { name: "Shogun: Asked for in Seerr · checking" })).toBeInTheDocument();
    expect(within(journey).queryByText("Waiting Film")).toBeNull();

    const activity = screen.getByRole("region", { name: /^activity/i });
    expect(within(activity).getByText(/Imported into the library/)).toBeInTheDocument();
    expect(within(activity).getByRole("link", { name: /Sintel/ })).toHaveAttribute("href", "/movies/m9");
    // No pound of old internal words.
    expect(document.body.textContent ?? "").not.toMatch(/undefined|NaN|importFailed|notTransferring|filesystem\.import/);
  });

  it("says each thing once: the indexers and clients are one card, the next jobs and airing episodes are one list", async () => {
    seedBusy();
    renderTab(fullSummary);
    expect(await screen.findByRole("link", { name: /^Nyaa\.si: Down/ })).toHaveAttribute("href", "/?tab=system");
    const next = screen.getByRole("region", { name: "Next" });
    await waitFor(() => expect(next.textContent).toContain("Lioness S03E02 airs"));
  });

  it("says something true on a new install, with no button and no blank panel", () => {
    const { container } = renderTab(emptySummary);
    expect(screen.getByText("Nothing moving right now")).toBeInTheDocument();
    expect(screen.getAllByText("Nothing has arrived yet today.")).toHaveLength(1);
    expect(within(screen.getByRole("region", { name: "Turned down today" })).getByText("0 releases")).toBeInTheDocument();
    // An empty library says so and offers the next step, in a slim row under the band.
    expect(screen.getByText("Nothing in your library yet.")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Add a movie" })).toHaveAttribute("href", "/movies?add=true");
    expect(screen.getByText(/Nothing has happened yet/)).toBeInTheDocument();
    expect(screen.getByText("nothing is scheduled")).toBeInTheDocument();
    expect(container.textContent).not.toMatch(/undefined|NaN/);
  });

  it("renders before the summary has arrived without inventing numbers", () => {
    const { container } = renderTab(undefined);
    expect(container.textContent).toContain("0 MB/s");
    expect(container.textContent).not.toMatch(/undefined|NaN/);
  });
});
