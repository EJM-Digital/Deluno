import { QueryClientProvider } from "@tanstack/react-query";
import { renderHook, waitFor } from "@testing-library/react";
import { createElement, type ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { emptySummary, fullSummary } from "../routes/dashboard/summary-fixtures";

/**
 * #1082: the sidebar's "Needs a look" number and the Dashboard's Needs you
 * number come from one function over one set of stored answers, so they are
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
  const sidebar = renderHook(() => useAttention(45_000));
  const wrapper = ({ children }: { children: ReactNode }) => createElement(QueryClientProvider, { client: queryClient }, children);
  const dashboard = renderHook(() => {
    const summary = useDashboardSummary();
    const setup = useSetupState();
    return useDashboardAttention(summary.data, setup.needs);
  }, { wrapper });
  return { sidebar, dashboard };
}

async function agree() {
  const { sidebar, dashboard } = bothCounts();
  await waitFor(() => expect(sidebar.result.current.loading).toBe(false), { timeout: 4_000 });
  await waitFor(() => expect(dashboard.result.current.counts.overview).toBe(sidebar.result.current.total), { timeout: 4_000 });
  return { sidebar: sidebar.result.current, dashboard: dashboard.result.current };
}

describe("the sidebar and the Dashboard count the same problems", () => {
  beforeEach(() => {
    queryClient.clear();
    mocks.data = {};
  });

  it("agree on a new install: nothing", async () => {
    mocks.data = { summary: emptySummary() };
    const { sidebar, dashboard } = await agree();
    expect(dashboard.counts.overview).toBe(sidebar.total);
    expect(sidebar.total).toBe(0);
    expect(sidebar.summary).toBe("");
  });

  it("agree on a busy install", async () => {
    mocks.data = { summary: fullSummary() };
    const { sidebar, dashboard } = await agree();
    expect(sidebar.total).toBe(dashboard.counts.overview);
    expect(sidebar.total).toBeGreaterThan(0);
    // The card's sentence is made of the Overview's own rows.
    expect(dashboard.items.some((item) => sidebar.summary.startsWith(item.title))).toBe(true);
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
    const { sidebar, dashboard } = await agree();
    expect(sidebar.total).toBe(dashboard.counts.overview);
    expect(dashboard.items.filter((item) => /Nyaa/.test(item.title))).toHaveLength(1);
  });
});
