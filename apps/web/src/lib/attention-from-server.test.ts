import { renderHook, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { serverRow, summaryOf } from "./attention-summary-fixtures";

/**
 * #1185: the bell's number is the one the server counts (`GET /api/attention/summary`), the same function the tray
 * reads in-process. The browser adds nothing up: whatever the server answers is what the bell shows, rows and count.
 */
const mocks = vi.hoisted(() => ({ summary: null as unknown, calls: [] as string[] }));

vi.mock("./use-signalr", () => ({
  RealtimeGroups: { Dashboard: "dashboard", Activity: "activity" },
  useSignalREvent: () => undefined
}));
vi.mock("./failed-jobs", () => ({ fetchFailedJobs: () => Promise.resolve([]) }));
vi.mock("./api", async (importOriginal) => ({
  ...(await importOriginal<typeof import("./api")>()),
  fetchJson: (path: string) => {
    mocks.calls.push(path);
    if (path === "/api/attention/summary") return Promise.resolve(mocks.summary);
    if (path.includes("import-recovery")) return Promise.resolve({ openCount: 0, recentCases: [] });
    if (path.endsWith("/telemetry")) return Promise.resolve({ summary: {}, clients: [] });
    if (path.endsWith("/wanted")) return Promise.resolve({ totalWanted: 0 });
    return Promise.resolve([]);
  },
  fetchPageItems: () => Promise.resolve([])
}));

const { useAttention } = await import("./use-attention");
const { queryClient } = await import("./route-cache");

async function bell() {
  const { result } = renderHook(() => useAttention());
  await waitFor(() => expect(result.current.loading).toBe(false), { timeout: 4_000 });
  await waitFor(() => expect(mocks.calls).toContain("/api/attention/summary"));
  return result;
}

describe("the bell shows the server's count and rows", () => {
  beforeEach(() => {
    queryClient.clear();
    mocks.calls = [];
    mocks.summary = null;
  });

  it("is the server's number, whatever the lists the sidebar keeps would add up to", async () => {
    mocks.summary = summaryOf(
      [serverRow({ id: "outage", kind: "outage", tone: "bad" }), serverRow({ id: "lanes", kind: "lanes", count: 3 })],
      [serverRow({ id: "update", kind: "update" }), serverRow({ id: "connection-update:downloadClient:q", kind: "connection-update" })],
      { leftovers: 4, suggestions: 2 }
    );

    const result = await bell();
    await waitFor(() => expect(result.current.total).toBe(4));

    expect(result.current.summary.rows.map((row) => row.id)).toEqual(["outage", "lanes"]);
    // The ready updates, leftovers and suggestions are listed or noted, and are in no number.
    expect(result.current.summary.notices).toHaveLength(2);
    expect(result.current.summary.leftovers).toBe(4);
    expect(result.current.total).toBe(result.current.summary.count);
  });

  it("is nothing on a quiet install", async () => {
    mocks.summary = summaryOf([]);
    const result = await bell();
    expect(result.current.total).toBe(0);
    expect(result.current.summary.rows).toEqual([]);
  });

  it("does not make up a number when the server's answer is not a summary", async () => {
    mocks.summary = [];
    const result = await bell();
    expect(result.current.total).toBe(0);
    expect(result.current.summary.rows).toEqual([]);
  });
});
