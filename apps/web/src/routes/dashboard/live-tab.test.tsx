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
  it("has the library counts and the scheduled tasks on System, not here, and no Needs you: problems are in the bell", () => {
    renderTab(fullSummary);
    expect(screen.queryByText("Upgradable")).toBeNull();
    expect(screen.queryByRole("region", { name: /needs you/i })).toBeNull();
    expect(screen.queryByRole("region", { name: /airing soon/i })).toBeNull();
    expect(screen.queryByText(/Protected you/)).toBeNull();
    // The top band is three cards of one template, then the journey, the shelf and what was turned down, and the right column.
    for (const name of ["Speed", "Working on now", "Next", "The journey", "Arrived today", "Turned down today", "Indexers", "Download clients", "Activity"]) {
      expect(screen.getByRole("region", { name })).toBeInTheDocument();
    }
    expect(screen.queryByRole("region", { name: /^(today|latest)/i })).toBeNull();
  });

  it("puts the right column beside the main column on a wide screen and under it below, with the band first", () => {
    const { container } = renderTab(fullSummary);
    const columns = container.querySelector<HTMLElement>("[data-page-layout] > div.grid");
    expect(columns).not.toBeNull();
    const [main, side] = Array.from(columns!.children);
    expect(within(main as HTMLElement).getByRole("region", { name: "Speed" })).toBeInTheDocument();
    expect(within(main as HTMLElement).getByRole("region", { name: "The journey" })).toBeInTheDocument();
    expect(within(side as HTMLElement).getByRole("region", { name: "Indexers" })).toBeInTheDocument();
    expect(within(side as HTMLElement).getByRole("region", { name: "Activity" })).toBeInTheDocument();
    // The band is the first thing in the main column, three cards that stack on a phone.
    expect(main.firstElementChild?.className).toContain("lb-now");
  });

  it("shows the journey with a stuck download amber, a Seerr title tagged, the time left, and no button to settle anything", async () => {
    seedBusy();
    lists["/api/dashboard/live-extras"] = { asOfUtc: new Date().toISOString(), sinceUtc: "", arrivals: [], importsToday: 0, importedBytesToday: 0, upgrades: [], protected: { refusedToday: 0, unsafeToday: 0, latest: [] }, retentionReclaimBytes: 0, requests: [{ mediaType: "movie", entityId: "m7", title: "Shogun", status: "added", requestedBy: "Sam", createdUtc: "2026-09-30T00:00:00Z", updatedUtc: "2026-09-30T00:00:00Z" }] };
    const summary = { ...fullSummary, activity: { ...fullSummary.activity!, recent: [{ ...fullSummary.activity!.recent[0], relatedEntityType: "movie", relatedEntityId: "m9" }] } };
    renderTab(summary);

    const journey = screen.getByRole("region", { name: "The journey" });
    expect(await within(journey).findByText("Photographer (2026)")).toBeInTheDocument();
    expect(within(journey).getByRole("link", { name: /^Photographer \(2026\): 61% · 1.8 MB\/s · Deluge/ })).toBeInTheDocument();
    // The download that has stopped stays on the journey, amber, and says what Deluno is doing about it.
    const stuck = await within(journey).findByRole("link", { name: "The Kid (1921): Stuck at 34% · not moving" });
    expect(stuck.closest("li")).toHaveAttribute("data-journey-card");
    expect(within(journey).queryByRole("button")).toBeNull();
    // Hand-offs, import cases and failed jobs are the bell's, not cards here.
    expect(within(journey).queryByText("An import did not finish")).toBeNull();
    expect(within(journey).queryByText("Say Yes to the Dress")).toBeNull();
    // The request that is still being searched for is the first station, tagged as Seerr's.
    expect(await within(journey).findByRole("link", { name: "Shogun: Asked for in Seerr · looking" })).toBeInTheDocument();

    const activity = screen.getByRole("region", { name: /^activity/i });
    expect(within(activity).getByText(/Imported into the library/)).toBeInTheDocument();
    expect(within(activity).getByRole("link", { name: /Sintel/ })).toHaveAttribute("href", "/movies/m9");
    // No pound of old internal words.
    expect(document.body.textContent ?? "").not.toMatch(/undefined|NaN|importFailed|notTransferring|filesystem\.import/);
  });

  it("says each thing once: the indexers and clients are tiles, the next jobs and airing episodes are one list", async () => {
    seedBusy();
    renderTab(fullSummary);
    expect(await screen.findByRole("link", { name: /^Nyaa\.si: not answering/ })).toHaveAttribute("href", "/?tab=system");
    const next = screen.getByRole("region", { name: "Next" });
    await waitFor(() => expect(next.textContent).toContain("Lioness S03E02 airs"));
  });

  it("says something true on a new install, with no button and no blank panel", () => {
    const { container } = renderTab(emptySummary);
    expect(screen.getByText("Nothing moving right now")).toBeInTheDocument();
    expect(screen.getByText("Nothing has arrived yet today.")).toBeInTheDocument();
    expect(screen.getByText("Nothing has been turned down yet today.")).toBeInTheDocument();
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
