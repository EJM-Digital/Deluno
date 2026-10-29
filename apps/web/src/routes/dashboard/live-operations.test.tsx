import { QueryClientProvider } from "@tanstack/react-query";
import { act, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { queryClient } from "../../lib/route-cache";
import { fullSummary, emptySummary } from "./downloads-fixtures";
import { LiveTab } from "./live-tab";
import { liveSources } from "./sources";

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

afterEach(() => vi.useRealTimers());

function working() {
  return screen.getByRole("region", { name: "Working on now" });
}

describe("Working on now", () => {
  it("lists a grab on its way and an import under way, and drops each when its end arrives", async () => {
    renderTab(emptySummary);
    expect(within(working()).getByText("Nothing is running right now. No search has run yet.")).toBeInTheDocument();

    send("DispatchGrabAttempt", { dispatchId: "d1", releaseName: "Sintel.2010.1080p", clientId: "c1", clientName: "Deluge", timestamp: new Date().toISOString() });
    const grab = await within(working()).findByRole("link", { name: /Sending Sintel\.2010\.1080p to Deluge/ });
    expect(grab).toHaveAttribute("href", "/queue");
    expect(within(working()).getByText("thing running")).toBeInTheDocument();

    send("DispatchImportStarted", { dispatchId: "d2", releaseName: "Big.Buck.Bunny.2008", mediaType: "movie", timestamp: new Date().toISOString() });
    const imp = await within(working()).findByRole("link", { name: /Importing/ });
    expect(imp).toHaveAttribute("href", "/queue");
    expect(within(working()).getByText("things running")).toBeInTheDocument();

    send("DispatchGrabCompleted", { dispatchId: "d1", releaseName: "Sintel.2010.1080p", clientId: "c1", succeeded: true, message: null, timestamp: new Date().toISOString() });
    await waitFor(() => expect(grab).not.toBeInTheDocument());
    send("DispatchImportCompleted", { dispatchId: "d2", releaseName: "Big.Buck.Bunny.2008", succeeded: true, importedPath: "/m", failureReason: null, timestamp: new Date().toISOString() });
    await waitFor(() => expect(imp).not.toBeInTheDocument());
  });

  it("drops a grab that never reports its end once ten minutes have passed", async () => {
    vi.useFakeTimers({ toFake: ["Date"], now: new Date("2026-10-01T10:00:00Z") });
    renderTab(emptySummary);
    send("DispatchGrabAttempt", { dispatchId: "d1", releaseName: "Sintel.2010.1080p", clientId: "c1", clientName: "Deluge", timestamp: new Date().toISOString() });
    const old = await within(working()).findByRole("link", { name: /Sending Sintel\.2010\.1080p to Deluge/ });

    // Nine minutes on, another grab arrives and the first is still listed.
    vi.setSystemTime(new Date("2026-10-01T10:09:00Z"));
    send("DispatchGrabAttempt", { dispatchId: "d2", releaseName: "Moon.2009.1080p", clientId: "c1", clientName: "Deluge", timestamp: new Date().toISOString() });
    await within(working()).findByRole("link", { name: /Sending Moon\.2009\.1080p to Deluge/ });
    expect(old).toBeInTheDocument();

    // Eleven minutes after it began, the next push drops the one that never ended.
    vi.setSystemTime(new Date("2026-10-01T10:11:00Z"));
    send("DispatchGrabAttempt", { dispatchId: "d3", releaseName: "Solaris.1972.1080p", clientId: "c1", clientName: "Deluge", timestamp: new Date().toISOString() });
    await within(working()).findByRole("link", { name: /Sending Solaris\.1972\.1080p to Deluge/ });
    expect(within(working()).queryByRole("link", { name: /Sending Sintel/ })).toBeNull();
    expect(within(working()).getByRole("link", { name: /Sending Moon/ })).toBeInTheDocument();
  });

  it("each row of a running search and of the background jobs links to where it can be watched", async () => {
    const summary = {
      ...fullSummary,
      automation: { ...fullSummary.automation!, libraries: [{ ...fullSummary.automation!.libraries[0], status: "running" }] },
      system: { ...fullSummary.system!, jobs: { running: 2, queued: 0, failed: 0 } }
    };
    renderTab(summary);

    expect(await within(working()).findByRole("link", { name: /Searching Movies for what is missing/ })).toHaveAttribute("href", "/search-cycles");
    expect(within(working()).getByRole("link", { name: /2 background jobs are running/ })).toHaveAttribute("href", "/activity?status=in-progress");
  });
});

describe("the shelf and counters follow the newest Activity row", () => {
  const row = (id: string, category: string) => ({ id, message: "Something happened", label: null, severity: "info", category, createdUtc: new Date().toISOString() });
  const liveExtrasReads = (spy: ReturnType<typeof vi.spyOn>) => spy.mock.calls.filter(([filter]: unknown[]) => JSON.stringify((filter as { queryKey?: unknown }).queryKey) === JSON.stringify(liveSources.liveExtras.key));

  it.each(["filesystem.import.completed", "blocklist.release.refused", "seerr.request.added", "library.upgrade.imported"])("a new %s row asks the shelf and counters to read again at once", async (category) => {
    renderTab(emptySummary);
    const invalidate = vi.spyOn(queryClient, "invalidateQueries");
    send("ActivityEventAdded", row("a1", category));
    await waitFor(() => expect(liveExtrasReads(invalidate)).toHaveLength(1));
    invalidate.mockRestore();
  });

  it("a row about something else leaves the shelf and counters alone", async () => {
    renderTab(emptySummary);
    send("ActivityEventAdded", row("a0", "searching.cycle.started"));
    await screen.findByText("Something happened");
    const invalidate = vi.spyOn(queryClient, "invalidateQueries");
    send("ActivityEventAdded", row("a2", "searching.cycle.completed"));
    await screen.findAllByText("Something happened");
    expect(liveExtrasReads(invalidate)).toHaveLength(0);
    invalidate.mockRestore();
  });
});
