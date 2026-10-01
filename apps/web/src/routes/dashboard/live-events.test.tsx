import { QueryClientProvider } from "@tanstack/react-query";
import { act, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { queryClient } from "../../lib/route-cache";
import { fullSummary, emptySummary } from "./downloads-fixtures";
import { LiveTab } from "./live-tab";

/**
 * #1087: the Live tab moves only when something real changes. Each test sends
 * the same events the server sends and checks what the screen then says.
 */
const handlers = vi.hoisted(() => new Map<string, Set<(payload: unknown) => void>>());
vi.mock("../../lib/use-signalr", async (importOriginal) => {
  const { useEffect, useRef } = await import("react");
  return {
    ...(await importOriginal<typeof import("../../lib/use-signalr")>()),
    useSignalREvent: (name: string, _subjects: unknown, handler: (payload: unknown) => void) => {
      const latest = useRef(handler);
      useEffect(() => { latest.current = handler; });
      useEffect(() => {
        const call = (payload: unknown) => latest.current(payload);
        const set = handlers.get(name) ?? new Set();
        set.add(call);
        handlers.set(name, set);
        return () => { set.delete(call); };
      }, [name]);
    }
  };
});
vi.mock("../../lib/use-auth", () => ({ authedFetch: vi.fn(() => Promise.resolve({ ok: true, json: async () => ({}) })) }));

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

function send(name: string, payload: unknown) {
  act(() => {
    for (const handler of handlers.get(name) ?? []) handler(payload);
  });
}

function seed() {
  lists["/api/download-clients/telemetry"] = {
    capturedUtc: "2026-09-30T10:00:00Z",
    summary: {},
    clients: [{
      clientId: "c1", clientName: "Deluge",
      queue: [{ id: "q1", clientId: "c1", clientName: "Deluge", title: "Photographer (2026)", releaseName: "Photographer.2026.1080p", status: "downloading", progress: 61, speedMbps: 1.8, etaSeconds: 540, errorMessage: null }]
    }]
  };
}

function renderTab(summary = emptySummary) {
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
  handlers.clear();
  for (const key of Object.keys(lists)) delete lists[key];
});

describe("Live follows real changes", () => {
  it("moves a download's bar and speed when the server pushes a reading, and not before", async () => {
    seed();
    renderTab(fullSummary);
    const downloads = screen.getByRole("region", { name: /^every title/i });
    expect((await within(downloads).findAllByText("61%")).length).toBeGreaterThan(0);
    const bar = () => downloads.querySelector<HTMLElement>(".live-fill")?.style.width;
    expect(bar()).toBe("61%");

    send("DownloadProgress", { id: "q1", title: "Photographer (2026)", progress: 88, speedMbps: 5.5, eta: null, status: "downloading" });
    await waitFor(() => expect(within(downloads).getAllByText("88%").length).toBeGreaterThan(0));
    expect(bar()).toBe("88%");
    expect(downloads.textContent).toContain("5.5 MB/s");
  });

  it("puts a new activity row at the top the moment it arrives, once, and marks it as arrived", async () => {
    renderTab(emptySummary);
    const latest = screen.getByRole("region", { name: /^activity/i });
    expect(within(latest).getByText(/Nothing has happened yet/)).toBeInTheDocument();

    const event = { id: "evt-1", message: "Imported Sintel (2010).", category: "filesystem.import.completed", label: "Imported into the library", severity: "info", createdUtc: new Date().toISOString() };
    send("ActivityEventAdded", event);
    send("ActivityEventAdded", event);

    const rows = await within(latest).findAllByRole("link", { name: /Imported Sintel/ });
    expect(rows).toHaveLength(1);
    expect(rows[0].closest("li")).toHaveClass("live-stream-new");
    expect(within(latest).getByText("Imported into the library")).toBeInTheDocument();
    expect(rows[0].textContent).toMatch(/just now/);
    expect(within(latest).queryByText(/Nothing has happened yet/)).toBeNull();
  });

  it("shows a grab on its way and an import under way as cards, and takes them away when they end", async () => {
    renderTab(emptySummary);
    const board = screen.getByRole("region", { name: /^every title/i });
    // Idle says something true rather than nothing.
    expect(within(board).getByText("Nothing moving right now")).toBeInTheDocument();

    send("DispatchGrabAttempt", { dispatchId: "d1", releaseName: "Sintel.2010.1080p", clientId: "c1", clientName: "Deluge", timestamp: new Date().toISOString() });
    const grab = await within(board).findByRole("link", { name: /Sintel \(2010\): Sending to Deluge/ });
    expect(within(within(board).getByRole("group", { name: "Grabbed" })).getByText("Sintel (2010)")).toBeInTheDocument();
    expect(within(board).queryByText("Nothing moving right now")).toBeNull();
    send("DispatchGrabCompleted", { dispatchId: "d1", releaseName: "Sintel.2010.1080p", clientId: "c1", succeeded: true, message: null, timestamp: new Date().toISOString() });
    await waitFor(() => expect(grab).not.toBeInTheDocument());

    send("DispatchImportStarted", { dispatchId: "d2", releaseName: "Big.Buck.Bunny.2008", mediaType: "movie", timestamp: new Date().toISOString() });
    expect(await within(within(board).getByRole("group", { name: "Importing" })).findByText("Big Buck Bunny (2008)")).toBeInTheDocument();
    send("DispatchImportCompleted", { dispatchId: "d2", releaseName: "Big.Buck.Bunny.2008", succeeded: true, importedPath: "/m", failureReason: null, timestamp: new Date().toISOString() });
    await waitFor(() => expect(within(board).queryByText("Big Buck Bunny (2008)")).toBeNull());
  });

  it("says a running search in Right now, and the stages' counts from the summary while they are true", () => {
    const summary = structuredClone(fullSummary);
    summary.automation!.libraries = [{ libraryId: "l1", name: "Movies", mediaType: "movies", status: "running", nextSearchUtc: null, nextMissingSearchUtc: null, nextUpgradeSearchUtc: null, lastCompletedUtc: null, lastError: null }];
    summary.downloads!.processing = 2;
    renderTab(summary);
    const board = screen.getByRole("region", { name: /^every title/i });
    expect(within(screen.getByRole("region", { name: "Doing now" })).getByText(/Searching Movies/)).toBeInTheDocument();
    expect(within(board).getByRole("link", { name: "With the processor: 3" })).toBeInTheDocument();
  });

  it("does not animate anything by itself: the page has no looping animation of its own", () => {
    const { container } = renderTab(fullSummary);
    expect(container.querySelector(".animate-pulse, .animate-spin, .animate-ping, .animate-bounce")).toBeNull();
  });
});
