import { act, renderHook, waitFor } from "@testing-library/react";
import { QueryClientProvider } from "@tanstack/react-query";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const api = vi.hoisted(() => ({ fetchJson: vi.fn(), fetchPageItems: vi.fn() }));
vi.mock("../../lib/api", async (importOriginal) => ({ ...(await importOriginal<typeof import("../../lib/api")>()), fetchJson: api.fetchJson, fetchPageItems: api.fetchPageItems }));

const { fetchLiveExtras, liveSources, readDashboardAgain, refreshLiveExtrasNow, refreshSummary, setupNeedsFromCache, setupSources, storedLists, summaryQuery, SUMMARY_KEY, useDashboardSummary, useSetupState } = await import("./sources");
const { queryClient } = await import("../../lib/route-cache");
const { sharedSources } = await import("../../lib/shared-sources");

const wrapper = ({ children }: { children: ReactNode }) => <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;

beforeEach(() => { api.fetchJson.mockReset(); api.fetchPageItems.mockReset(); queryClient.clear(); });
afterEach(() => { vi.useRealTimers(); });

describe("the Live extras for the person's own day", () => {
  it("asks from their local midnight, and answers what the server sent", async () => {
    const extras = { arrivals: [] };
    api.fetchJson.mockResolvedValue(extras);
    expect(await fetchLiveExtras()).toBe(extras);
    const midnight = new Date();
    midnight.setHours(0, 0, 0, 0);
    expect(api.fetchJson).toHaveBeenCalledWith(`/api/dashboard/live-extras?sinceUtc=${encodeURIComponent(midnight.toISOString())}`);
  });

  it("answers nothing from an older server that sends no arrivals", async () => {
    api.fetchJson.mockResolvedValueOnce({ other: 1 });
    expect(await fetchLiveExtras()).toBeNull();
    api.fetchJson.mockResolvedValueOnce(null);
    expect(await fetchLiveExtras()).toBeNull();
  });

  it("fails when the read fails, so the cache keeps the board it shows instead of an empty one (#1508)", async () => {
    api.fetchJson.mockRejectedValueOnce(new Error("offline"));
    await expect(fetchLiveExtras()).rejects.toThrow("offline");
    const first = { arrivals: [{ id: "a" }] };
    api.fetchJson.mockResolvedValueOnce(first);
    await queryClient.prefetchQuery({ queryKey: liveSources.liveExtras.key, queryFn: liveSources.liveExtras.fetch, retry: false });
    api.fetchJson.mockRejectedValueOnce(new Error("offline"));
    await queryClient.refetchQueries({ queryKey: liveSources.liveExtras.key });
    expect(queryClient.getQueryData(liveSources.liveExtras.key)).toBe(first);
  });

  it("asks past the server's remembered copy once after a pushed change, and then no more", async () => {
    api.fetchJson.mockResolvedValue({ arrivals: [] });
    const invalidate = vi.spyOn(queryClient, "invalidateQueries").mockResolvedValue();
    await refreshLiveExtrasNow();
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ["src", "dashboard-live-extras"], exact: true });
    invalidate.mockRestore();

    await fetchLiveExtras();
    await fetchLiveExtras();
    expect(api.fetchJson.mock.calls[0][0]).toMatch(/&fresh=true$/);
    expect(api.fetchJson.mock.calls[1][0]).not.toContain("fresh");
  });
});

describe("reading the dashboard again after the connection returns (#1518)", () => {
  it("re-reads every dashboard list once, the Live extras past the server's remembered copy", async () => {
    const invalidate = vi.spyOn(queryClient, "invalidateQueries").mockResolvedValue();
    await readDashboardAgain();
    expect(invalidate.mock.calls.map((call) => call[0])).toEqual([{ queryKey: ["dashboard"] }, { queryKey: ["src"] }]);
    invalidate.mockRestore();
    api.fetchJson.mockResolvedValue({ arrivals: [] });
    await fetchLiveExtras();
    expect(api.fetchJson.mock.calls.at(-1)?.[0]).toMatch(/&fresh=true$/);
  });
});

describe("the summary", () => {
  it("is read once, from the summary address, and kept fresh for three seconds", async () => {
    expect(SUMMARY_KEY).toEqual(["dashboard", "summary"]);
    expect(summaryQuery).toMatchObject({ queryKey: ["dashboard", "summary"], staleTime: 3_000, refetchOnMount: "always" });
    api.fetchJson.mockResolvedValue({ ok: true });
    const { result } = renderHook(() => useDashboardSummary(), { wrapper });
    await waitFor(() => expect(result.current.data).toEqual({ ok: true }));
    expect(api.fetchJson).toHaveBeenCalledWith("/api/dashboard/summary");
  });

  it("asks again when told to", async () => {
    const invalidate = vi.spyOn(queryClient, "invalidateQueries").mockResolvedValue();
    await refreshSummary();
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ["dashboard", "summary"] });
    invalidate.mockRestore();
  });
});

describe("what each list is read from", () => {
  it.each([
    ["policySets", setupSources.policySets, "/api/policy-sets", []],
    ["qualityProfiles", setupSources.qualityProfiles, "/api/quality-profiles", []],
    ["upcoming", liveSources.upcoming, "/api/series/upcoming?take=20&hours=168", []],
    ["throughput", liveSources.throughput, "/api/download-clients/throughput?hours=6", { hours: 6, samples: [] }],
    ["scoreboard", liveSources.scoreboard, "/api/indexers/scoreboard?days=1", null]
  ] as const)("%s reads %s", async (_name, src, path, fallback) => {
    api.fetchJson.mockResolvedValue("answer");
    expect(await src.fetch()).toBe("answer");
    expect(api.fetchJson).toHaveBeenCalledWith(path);
    expect(src.fallback).toEqual(fallback);
  });

  it("reads the setup progress, starting from nothing done", async () => {
    api.fetchJson.mockResolvedValue("p");
    expect(await setupSources.setupProgress.fetch()).toBe("p");
    expect(api.fetchJson).toHaveBeenCalledWith("/api/setup/progress");
    expect(setupSources.setupProgress.fallback).toMatchObject({ lastCompletedStep: 0, isSkipped: false, isCompleted: false, updatedUtc: new Date(0).toISOString() });
  });

  it("reads the board's sent downloads and the newest Activity in pages", async () => {
    api.fetchPageItems.mockResolvedValue(["x"]);
    expect(await liveSources.boardDispatches.fetch()).toEqual(["x"]);
    expect(api.fetchPageItems.mock.calls[0][0]).toMatch(/^\/api\/download-dispatches\?pageSize=300/);
    expect(await liveSources.latestActivity.fetch()).toEqual(["x"]);
    expect(api.fetchPageItems.mock.calls.at(-1)?.[0]).toMatch(/^\/api\/activity\?pageSize=80&excludeCategories=.*job\.started/);
    expect(liveSources.latestActivity.fallback).toEqual([]);
  });

  it("shares the lists other screens already keep, with an empty start where the screen needs one", () => {
    expect(storedLists.telemetry.key).toEqual(sharedSources.telemetry.key);
    expect(storedLists.handoffs.key).toEqual(sharedSources.handoffs.key);
    expect(storedLists.dispatches.key).toEqual(sharedSources.dispatches.key);
    expect(storedLists.indexers.key).toEqual(sharedSources.indexers.key);
    expect(liveSources.dispatches.fallback).toEqual([]);
    expect(liveSources.handoffs.fallback).toEqual([]);
    expect(liveSources.indexers.fallback).toEqual([]);
    expect(liveSources.clients.fallback).toEqual([]);
    expect(liveSources.clients.key).toEqual(sharedSources.downloadClients.key);
    expect(setupSources.libraries.fallback).toEqual([]);
    expect(setupSources.indexers.fallback).toEqual([]);
    expect(setupSources.downloadClients.fallback).toEqual([]);
    expect(liveSources.liveExtras.fallback).toBeNull();
    expect(liveSources.liveExtras.fetch).toBe(fetchLiveExtras);
  });
});

describe("the setup needs", () => {
  it("come from whatever the cache holds, and are the same on the sidebar and the Dashboard", async () => {
    const fromCache = setupNeedsFromCache();
    expect(Array.isArray(fromCache)).toBe(true);
    api.fetchJson.mockImplementation(async (path: string) => (path === "/api/setup/progress" ? { lastCompletedStep: 0, isSkipped: false, isCompleted: false, updatedUtc: "2026-01-01T00:00:00Z" } : []));
    const { result } = renderHook(() => useSetupState(), { wrapper });
    await waitFor(() => expect(result.current.ready).toBe(true));
    expect(result.current.needs.map((n) => n.id)).toEqual(fromCache.map((n) => n.id));
    expect(result.current.status.steps.length).toBeGreaterThan(0);
    expect(result.current.progress).toMatchObject({ lastCompletedStep: 0 });
  });

  it("follow the cache when a list changes", async () => {
    api.fetchJson.mockImplementation(async (path: string) => (path === "/api/setup/progress" ? { lastCompletedStep: 1, isSkipped: false, isCompleted: false, updatedUtc: "2026-01-01T00:00:00Z" } : []));
    const { result } = renderHook(() => useSetupState(), { wrapper });
    await waitFor(() => expect(result.current.progress.lastCompletedStep).toBe(1));

    act(() => { queryClient.setQueryData(setupSources.setupProgress.key, { lastCompletedStep: 4, isSkipped: false, isCompleted: false, updatedUtc: "2026-01-02T00:00:00Z" }); });

    await waitFor(() => expect(result.current.progress.lastCompletedStep).toBe(4));
  });

  it("leave out what the checklist already has a step for, and the connection health item", () => {
    for (const need of setupNeedsFromCache()) expect(need.id).not.toBe("connection-health");
  });
});
