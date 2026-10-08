import { act, renderHook, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

/**
 * #937: `useAttention` used to call its own eleven-fetch `load()` from four
 * independent triggers -- the 45s ticker, every ActivityEventAdded in
 * FAILED_JOB_CATEGORIES, every HealthChanged, and a manual refresh -- with no
 * guard against one call landing while an earlier one was still in flight.
 * Under a slow backend (a loaded dev server, a throttled CI runner) that let
 * a burst of triggers start a new eleven-fetch batch on top of the one still
 * running, and the one after that on top of both, so the outstanding-request
 * count only grew. That is what turned an ordinary slow response into a page
 * that never goes network-idle -- see apps/web/tests/smoke/mobile-route-matrix.spec.ts.
 *
 * `vi.hoisted` mutable state, mirroring use-signalr.test.tsx: `vi.mock`
 * factories are hoisted above ordinary top-level `let`/`const`.
 */
const mocks = vi.hoisted(() => ({
  fetchJsonCalls: [] as string[],
  activityHandlers: [] as Array<(event: { category: string }) => void>,
  healthHandlers: [] as Array<() => void>,
  pushHandlers: {} as Record<string, Array<() => void>>,
  answer: false
}));

vi.mock("./use-signalr", () => ({
  RealtimeGroups: { Dashboard: "dashboard", Activity: "activity" },
  useSignalREvent: (event: string, _subject: unknown, handler: (payload: never) => void) => {
    if (event === "ActivityEventAdded") mocks.activityHandlers.push(handler as (event: { category: string }) => void);
    if (event === "HealthChanged") mocks.healthHandlers.push(handler as () => void);
    (mocks.pushHandlers[event] ??= []).push(handler as () => void);
  }
}));

vi.mock("./failed-jobs", () => ({
  fetchFailedJobs: () => Promise.resolve([])
}));

vi.mock("./api", async (importOriginal) => ({
  ...(await importOriginal<typeof import("./api")>()),
  ApiRequestError: class ApiRequestError extends Error {},
  fetchJson: (url: string) => {
    mocks.fetchJsonCalls.push(url);
    if (mocks.answer) return Promise.resolve([]);
    // Never resolves on its own -- the test resolves each batch explicitly,
    // by holding a reference to the promise's own resolver.
    return new Promise(() => {});
  },
  fetchPageItems: (url: string) => {
    mocks.fetchJsonCalls.push(url);
    return new Promise(() => {});
  }
}));

const { useAttention, attentionSourcesForReasons } = await import("./use-attention");
const { queryClient } = await import("./route-cache");

describe("useAttention", () => {
  beforeEach(() => {
    queryClient.clear();
    mocks.fetchJsonCalls = [];
    mocks.activityHandlers = [];
    mocks.healthHandlers = [];
    mocks.pushHandlers = {};
    mocks.answer = false;
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it("does not start a second batch of fetches while the first is still in flight", async () => {
    renderHook(() => useAttention());

    // The mount effect fires the first `load()`, which starts its batch of
    // fetches and then hangs (the mock never resolves) -- standing in for a
    // batch that is simply slow to come back.
    await waitFor(() => expect(mocks.fetchJsonCalls.length).toBeGreaterThan(0));
    const afterFirstBatch = mocks.fetchJsonCalls.length;

    // A burst of triggers arrives while that batch is still outstanding --
    // exactly what a stream of ActivityEventAdded/HealthChanged envelopes
    // does on a real install.
    act(() => {
      for (let i = 0; i < 5; i += 1) {
        mocks.activityHandlers.forEach((handler) => handler({ category: "job.failed" }));
        mocks.healthHandlers.forEach((handler) => handler());
      }
    });

    // No new fetch went out: the guard dropped every one of those triggers
    // rather than layering five more eleven-fetch batches on top of the first.
    expect(mocks.fetchJsonCalls.length).toBe(afterFirstBatch);
  });

  it("follows the pushes that move the count and runs no timer (#1179)", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
      renderHook(() => useAttention());
      await vi.waitFor(() => expect(mocks.fetchJsonCalls.length).toBeGreaterThan(0));
      for (const name of ["ActivityEventAdded", "HealthChanged", "ConnectionChecked", "AttentionChanged", "MovieChanged", "SeriesChanged", "IndexerChanged"]) {
        expect(mocks.pushHandlers[name]?.length, name).toBeGreaterThan(0);
      }

      // With the connection down there used to be a 45 s safety-net read; there is none now.
      const before = mocks.fetchJsonCalls.length;
      await vi.advanceTimersByTimeAsync(180_000);
      expect(mocks.fetchJsonCalls.length).toBe(before);
    } finally {
      vi.useRealTimers();
    }
  });

  it("reads only the lists a push names, and everything for a word it does not know (#1179)", () => {
    const keys = (reasons: string[]) => attentionSourcesForReasons(reasons)?.map((source) => source.key.join("/"));
    expect(keys(["noRelease"])).toEqual(["src/attention-no-release", expect.stringContaining("dashboard")]);
    expect(keys(["noRelease"])?.length).toBe(2);
    expect(keys(["heldReleases"])).toEqual([]);
    // A burst names several words: their lists are read once each.
    expect(keys(["jobs", "recovery"])?.filter((key) => key.includes("failed")).length).toBe(1);
    expect(attentionSourcesForReasons(["noRelease", "journey"])).toBeNull();
    expect(attentionSourcesForReasons(["client"])).toBeNull();
    expect(attentionSourcesForReasons(["all"])).toBeNull();
  });

  it("an open bell reads again only the no-release list and the summary after a push naming it, with no timer (#1179)", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
      mocks.answer = true;
      renderHook(() => useAttention());
      await vi.waitFor(() => expect(mocks.fetchJsonCalls.length).toBeGreaterThan(0));
      await vi.advanceTimersByTimeAsync(1_000);

      mocks.fetchJsonCalls = [];
      act(() => {
        (mocks.pushHandlers["AttentionChanged"] ?? []).forEach((handler) => (handler as unknown as (event: { reason: string }) => void)({ reason: "noRelease" }));
      });
      await vi.advanceTimersByTimeAsync(1_000);

      expect(mocks.fetchJsonCalls).toContain("/api/attention/no-release");
      expect(mocks.fetchJsonCalls.some((url) => url.includes("/api/jobs") || url.includes("indexers"))).toBe(false);
      expect(mocks.fetchJsonCalls.length).toBeLessThanOrEqual(2);
    } finally {
      vi.useRealTimers();
    }
  });
});
