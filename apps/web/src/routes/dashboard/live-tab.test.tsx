import { QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { queryClient } from "../../lib/route-cache";
import { computeAttention } from "./attention";
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
  it("has the library counts and the scheduled tasks on System, not here: Live opens on what Deluno is doing", () => {
    renderTab(fullSummary);
    expect(screen.queryByText("Upgradable")).toBeNull();
    expect(screen.getByRole("region", { name: /^needs you/i })).toBeInTheDocument();
    expect(screen.getByRole("region", { name: "Speed" })).toBeInTheDocument();
    expect(screen.getByRole("region", { name: "Doing now" })).toBeInTheDocument();
    expect(screen.getByRole("region", { name: "Next up" })).toBeInTheDocument();
    expect(screen.getByRole("region", { name: "Indexers" })).toBeInTheDocument();
    expect(screen.getByRole("region", { name: /^activity/i })).toBeInTheDocument();
    expect(screen.queryByRole("region", { name: /^(today|latest)/i })).toBeNull();
  });

  it("counts every problem a person has to settle once, in the Needs you strip, and settles each from its drawer", async () => {
    seedBusy();
    renderTab(fullSummary);
    const user = userEvent.setup();

    const strip = screen.getByRole("region", { name: "Needs you" });
    // The strip's number and the badge's number are the same thing.
    const now = computeAttention(fullSummary, {
      movieRecovery: lists["/api/movies/import-recovery"] as never,
      seriesRecovery: lists["/api/series/import-recovery"] as never,
      failedJobs: lists["/api/jobs"] as never,
      handoffs: lists["/api/integrations/processors/handoffs"] as never,
      telemetry: lists["/api/download-clients/telemetry"] as never
    }).counts.live;
    await waitFor(() => expect(within(strip).getByText(String(now))).toBeInTheDocument());
    // Grouped by kind: one chip each, one Review button each; nothing is a card on the journey.
    expect(await within(strip).findByText("1 hand-off refused")).toBeInTheDocument();
    expect(within(strip).getByText("1 stuck download")).toBeInTheDocument();
    expect(within(strip).getByText("1 import failed")).toBeInTheDocument();
    expect(within(strip).queryByRole("button", { name: /^(Retry|Give up|Search again)/ })).toBeNull();
    expect(within(screen.getByRole("region", { name: /^every title/i })).queryByRole("button")).toBeNull();

    // The hand-off the processor refused is a problem to settle, so it is not in the processor's stage.
    const board = screen.getByRole("region", { name: /^every title/i });
    expect(within(board).queryByRole("group", { name: "With the processor" })?.textContent ?? "").not.toContain("Metropolis");

    // Each group opens its own items with their own buttons.
    await user.click(within(strip).getByRole("button", { name: "Review 1 hand-off refused" }));
    const drawer = await screen.findByRole("dialog");
    expect(within(drawer).getByRole("button", { name: "Retry Metropolis (1927)" })).toBeInTheDocument();
    expect(within(drawer).getByRole("button", { name: "Give up on Metropolis (1927)" })).toBeInTheDocument();
    const names = within(drawer).getAllByRole("button").map((button) => button.getAttribute("aria-label") ?? button.textContent);
    expect(new Set(names).size).toBe(names.length);

    // The setup problems are on the other tab, not repeated here.
    expect(screen.queryByRole("button", { name: "Test Nyaa.si again" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Resume NZBGet" })).toBeNull();
  });

  it("shows the journey board with Search again, Airing soon and the activity in plain words", async () => {
    seedBusy();
    const summary = { ...fullSummary, activity: { ...fullSummary.activity!, recent: [{ ...fullSummary.activity!.recent[0], relatedEntityType: "movie", relatedEntityId: "m9" }] } };
    renderTab(summary);

    const downloads = screen.getByRole("region", { name: /^every title/i });
    expect(await within(downloads).findByText("Photographer (2026)")).toBeInTheDocument();
    expect(within(downloads).getAllByText("61%").length).toBeGreaterThan(0);
    // The stuck download is a problem: it is in the Needs you strip, not on the journey.
    expect(within(downloads).queryByText("The Kid (1921)")).toBeNull();
    expect(await within(screen.getByRole("region", { name: "Needs you" })).findByText("1 stuck download")).toBeInTheDocument();

    const coming = screen.getByRole("region", { name: /^airing soon/i });
    expect(await within(coming).findByRole("link", { name: /Lioness/ })).toHaveAttribute("href", "/tv/s1");

    const activity = screen.getByRole("region", { name: /^activity/i });
    expect(within(activity).getByText(/Imported into the library/)).toBeInTheDocument();
    expect(within(activity).getByRole("link", { name: /Sintel/ })).toHaveAttribute("href", "/movies/m9");
    expect(document.body.textContent ?? "").not.toMatch(/undefined|NaN|importFailed|notTransferring|filesystem\.import/);
  });

  it("sends the same requests Transfers does for retry and give up", async () => {
    seedBusy();
    renderTab(fullSummary);
    const user = userEvent.setup();

    // Four groups do not all fit the strip: the rest are one button that opens everything.
    await user.click(await screen.findByRole("button", { name: "Review all that needs you" }));
    await user.click(await screen.findByRole("button", { name: "Retry Say Yes to the Dress" }));
    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/movies/import-recovery/r1/retry", expect.objectContaining({ method: "POST" })));
    await user.click(screen.getByRole("button", { name: "Give up on Say Yes to the Dress" }));
    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/movies/import-recovery/r1/dismiss", expect.objectContaining({ method: "POST" })));
  });

  it("says something true on a new install, with no button and no blank panel", () => {
    const { container } = renderTab(emptySummary);
    expect(screen.getByText("Nothing needs you.")).toBeInTheDocument();
    expect(screen.getByText("Nothing moving right now")).toBeInTheDocument();
    expect(screen.getByText("Nothing has arrived yet today.")).toBeInTheDocument();
    // An empty library says so and offers the next step, in a slim row under the band.
    expect(screen.getByText("Nothing in your library yet.")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Add a movie" })).toHaveAttribute("href", "/movies?add=true");
    expect(screen.getByText(/Nothing you follow airs in the next 7 days/)).toBeInTheDocument();
    expect(screen.getByText(/Nothing has happened yet/)).toBeInTheDocument();
    expect(container.textContent).not.toMatch(/undefined|NaN/);
  });

  it("renders before the summary has arrived without inventing numbers", () => {
    const { container } = renderTab(undefined);
    expect(container.textContent).toContain("0 MB/s");
    expect(container.textContent).not.toMatch(/undefined|NaN/);
  });
});
