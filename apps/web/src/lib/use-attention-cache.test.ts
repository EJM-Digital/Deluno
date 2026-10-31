import { act, renderHook, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

/**
 * #1081: the sidebar counts read through the shared cache. They never hold up
 * a screen change, they show the last count at once on a return, and the
 * leftover-folders walk (which In progress refreshes while it is open) is not part
 * of the 45 second poll.
 */
const mocks = vi.hoisted(() => ({ calls: [] as string[] }));

vi.mock("./use-signalr", () => ({
  RealtimeGroups: { Dashboard: "dashboard", Activity: "activity" },
  useSignalREvent: () => undefined
}));
vi.mock("./failed-jobs", () => ({ fetchFailedJobs: () => { mocks.calls.push("failed-jobs"); return Promise.resolve([]); } }));
// The In progress badge counts held releases too (#1448); their source reads through ./api/client, not the ./api mocked below.
vi.mock("./held-releases", async (importOriginal) => {
  const actual = await importOriginal<typeof import("./held-releases")>();
  return { ...actual, heldReleasesSource: { ...actual.heldReleasesSource, fetch: () => { mocks.calls.push("/api/attention/held-releases"); return Promise.resolve([]); } } };
});
vi.mock("./api", async (importOriginal) => ({
  ...(await importOriginal<typeof import("./api")>()),
  ApiRequestError: class ApiRequestError extends Error {},
  fetchJson: (path: string) => {
    mocks.calls.push(path);
    if (path.endsWith("/wanted")) return Promise.resolve({ totalWanted: 3 });
    if (path.includes("import-recovery")) return Promise.resolve({ openCount: 0, recentCases: [] });
    if (path.endsWith("/telemetry")) return Promise.resolve({ summary: { activeCount: 0, queuedCount: 0, stalledCount: 0, importReadyCount: 0, waitingForProcessorCount: 0 }, clients: [] });
    if (path.endsWith("/summary")) return Promise.resolve({ unsnoozedProblemCount: 2 });
    return Promise.resolve([]);
  },
  fetchPageItems: (path: string) => { mocks.calls.push(path); return Promise.resolve([]); }
}));

const { useAttention } = await import("./use-attention");
const { queryClient } = await import("./route-cache");

describe("useAttention reads through the shared cache (#1081)", () => {
  beforeEach(() => {
    queryClient.clear();
    mocks.calls = [];
  });

  it("does not ask for the leftover folders again on a refresh once something has read them", async () => {
    const { result } = renderHook(() => useAttention());
    await waitFor(() => expect(result.current.loading).toBe(false), { timeout: 3_000 });
    expect(mocks.calls.filter((path) => path.endsWith("/orphans"))).toHaveLength(1);

    await act(async () => { await result.current.refresh(); });

    expect(mocks.calls.filter((path) => path.endsWith("/orphans"))).toHaveLength(1);
    // The rest are asked again: an event said something changed.
    expect(mocks.calls.filter((path) => path.endsWith("/telemetry")).length).toBeGreaterThanOrEqual(2);
  });

  it("counts from what is already known, at once, when the layout mounts again", async () => {
    const first = renderHook(() => useAttention());
    await waitFor(() => expect(first.result.current.healthProblems).toBe(2), { timeout: 3_000 });
    first.unmount();
    mocks.calls = [];

    const second = renderHook(() => useAttention());

    // Before any request of its own has been answered.
    expect(second.result.current.loading).toBe(false);
    expect(second.result.current.healthProblems).toBe(2);
    expect(second.result.current.movieWanted).toBe(3);
  });

  it("does not start its requests in the same breath as the screen that is opening", () => {
    renderHook(() => useAttention());
    expect(mocks.calls).toEqual([]);
  });
});
