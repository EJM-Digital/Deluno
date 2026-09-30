import { QueryClientProvider } from "@tanstack/react-query";
import { renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { fullSummary } from "./summary-fixtures";

const api = vi.hoisted(() => ({ fetchJson: vi.fn() }));
vi.mock("../../lib/api", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../../lib/api")>()),
  fetchJson: api.fetchJson,
  fetchPageItems: vi.fn(() => Promise.resolve([]))
}));

const { useDashboardAttention } = await import("./use-dashboard-attention");
const { queryClient } = await import("../../lib/route-cache");
const { sharedSources } = await import("../../lib/shared-sources");

const wrapper = ({ children }: { children: ReactNode }) => <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;

const orphans = [{ name: "a" }, { name: "b" }, { name: "c" }];

beforeEach(() => {
  queryClient.clear();
  api.fetchJson.mockReset();
  api.fetchJson.mockImplementation((path: string) => {
    if (path.endsWith("/orphans")) return Promise.resolve(orphans);
    if (path.endsWith("/health/summary")) return Promise.resolve({ problemCount: 0, unsnoozedProblemCount: 0, unknownCount: 0, suggestionCount: 0, lastRunUtc: null });
    if (path.includes("import-recovery")) return Promise.resolve({ openCount: 0, recentCases: [] });
    if (path.endsWith("/telemetry")) return Promise.resolve({ summary: {}, clients: [] });
    return Promise.resolve([]);
  });
});

describe("the Dashboard's count does not depend on which screens were opened first (#1087)", () => {
  it("is the same with the other screens' lists cached as it is with none, and does not count the leftover folders either time", async () => {
    const summary = fullSummary();

    // Nothing cached: the hook reads every list itself.
    const cold = renderHook(() => useDashboardAttention(summary), { wrapper });
    await waitFor(() => expect(cold.result.current.alsoWorthALook.leftovers).toBe(3));
    const coldCounts = cold.result.current.counts;
    cold.unmount();

    // Everything cached, as after visiting Downloads and Transfers.
    queryClient.setQueryData(sharedSources.orphans.key, orphans);
    const warm = renderHook(() => useDashboardAttention(summary), { wrapper });
    await waitFor(() => expect(warm.result.current.alsoWorthALook.leftovers).toBe(3));

    expect(warm.result.current.counts).toEqual(coldCounts);
  });
});
