import { QueryClientProvider } from "@tanstack/react-query";
import { renderHook, waitFor } from "@testing-library/react";
import { createElement, type ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { emptySummary, fullSummary } from "../routes/dashboard/summary-fixtures";

/**
 * #1087: the bell's number and the Dashboard's two tab badges
 * added together come from one function over one set of stored answers, so they are
 * the same number for the same data.
 */
const mocks = vi.hoisted(() => ({ data: {} as Record<string, unknown> }));

vi.mock("./use-signalr", () => ({
  RealtimeGroups: { Dashboard: "dashboard", Activity: "activity" },
  useSignalREvent: () => undefined
}));
vi.mock("./failed-jobs", () => ({ fetchFailedJobs: () => Promise.resolve(mocks.data.failedJobs ?? []) }));
vi.mock("./api", async (importOriginal) => ({
  ...(await importOriginal<typeof import("./api")>()),
  fetchJson: (path: string) => {
    const table: [string, unknown][] = [
      ["/api/dashboard/summary", mocks.data.summary],
      ["/api/indexers", mocks.data.indexers ?? []],
      ["/api/integrations/processors/connections", mocks.data.processors ?? []],
      ["/api/integrations/processors/handoffs", []],
      ["/api/download-clients/orphans", mocks.data.orphans ?? []],
      ["/api/download-clients/telemetry", { summary: { activeCount: 0, queuedCount: 0, stalledCount: 0, importReadyCount: 0, waitingForProcessorCount: 0 }, clients: [], items: [] }],
      ["import-recovery", { openCount: 0, recentCases: [] }],
      ["/api/system/health/summary", mocks.data.healthSummary ?? { problemCount: 0, unsnoozedProblemCount: 0, unknownCount: 0, suggestionCount: 0, lastRunUtc: null }],
      ["/wanted", { totalWanted: 0 }],
      ["/api/setup/progress", { lastCompletedStep: 9, isSkipped: false, isCompleted: true, updatedUtc: "2026-09-30T00:00:00Z" }]
    ];
    const hit = table.find(([key]) => path.includes(key));
    return Promise.resolve(hit ? hit[1] : []);
  },
  fetchPageItems: () => Promise.resolve([])
}));

const { useAttention } = await import("./use-attention");
const { queryClient } = await import("./route-cache");
const { useDashboardAttention } = await import("../routes/dashboard/use-dashboard-attention");
const { useDashboardSummary, useSetupState } = await import("../routes/dashboard/sources");

function bothCounts() {
  const bell = renderHook(() => useAttention());
  const wrapper = ({ children }: { children: ReactNode }) => createElement(QueryClientProvider, { client: queryClient }, children);
  const dashboard = renderHook(() => {
    const summary = useDashboardSummary();
    const setup = useSetupState();
    return useDashboardAttention(summary.data, setup.needs);
  }, { wrapper });
  return { bell, dashboard };
}

async function agree() {
  const { bell, dashboard } = bothCounts();
  await waitFor(() => expect(bell.result.current.loading).toBe(false), { timeout: 4_000 });
  await waitFor(() => expect(dashboard.result.current.total).toBe(bell.result.current.total), { timeout: 4_000 });
  return { bell: bell.result.current, dashboard: dashboard.result.current };
}

describe("the bell and the Dashboard count the same problems", () => {
  beforeEach(() => {
    queryClient.clear();
    mocks.data = {};
  });

  it("agree on a new install: nothing", async () => {
    mocks.data = { summary: emptySummary() };
    const { bell, dashboard } = await agree();
    expect(dashboard.total).toBe(bell.total);
    expect(bell.total).toBe(0);
    expect(bell.needs.items).toHaveLength(0);
  });

  it("agree on a busy install", async () => {
    mocks.data = { summary: fullSummary() };
    const { bell, dashboard } = await agree();
    expect(bell.total).toBe(dashboard.total);
    expect(bell.total).toBeGreaterThan(0);
    // The two badges added together are the bell's number, and the bell's rows are the Dashboard's.
    expect(dashboard.counts.live + dashboard.counts.system).toBe(bell.total);
    expect(bell.needs.items.map((item) => item.id)).toEqual(dashboard.items.map((item) => item.id));
  });

  it("agree on a rig-like install, and count an indexer that is down once", async () => {
    const summary = fullSummary();
    summary.health = {
      ...summary.health!,
      problems: 3,
      topProblems: [
        { id: "indexer:i1", title: "Nyaa.si (indexer)", category: "indexer", message: "nyaa.si answered HTTP 504", sinceUtc: null, link: "/indexers/indexers", snoozed: false },
        { id: "system:disk", title: "A drive is nearly full", category: "system", message: "Less than 5% is free.", sinceUtc: null, link: "/system", snoozed: false }
      ]
    };
    mocks.data = {
      summary,
      healthSummary: { problemCount: 3, unsnoozedProblemCount: 3, unknownCount: 0, suggestionCount: 0, lastRunUtc: null },
      indexers: [{ id: "i1", name: "Nyaa.si", isEnabled: true, healthStatus: "unreachable", lastHealthMessage: "nyaa.si answered HTTP 504" }],
      orphans: [{ name: "a" }, { name: "b" }],
      failedJobs: [{ id: "j1", jobType: "library.scan", status: "failed", attempts: 3, maxAttempts: 3, nextAttemptUtc: null, lastError: "x" }]
    };
    const { bell, dashboard } = await agree();
    expect(bell.total).toBe(dashboard.total);
    expect(dashboard.items.filter((item) => /Nyaa/.test(item.title))).toHaveLength(1);
  });

  it("do not count leftover folders or suggestions, on either side", async () => {
    mocks.data = {
      summary: emptySummary(),
      orphans: [{ name: "a" }, { name: "b" }, { name: "c" }],
      healthSummary: { problemCount: 0, unsnoozedProblemCount: 0, unknownCount: 0, suggestionCount: 4, lastRunUtc: null }
    };
    const { bell, dashboard } = await agree();
    expect(bell.total).toBe(0);
    expect(dashboard.total).toBe(0);
    expect(dashboard.alsoWorthALook).toEqual({ leftovers: 3, suggestions: 4 });
  });
});
