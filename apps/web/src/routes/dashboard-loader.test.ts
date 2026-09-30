import { beforeEach, describe, expect, it, vi } from "vitest";

/**
 * #1081, #1082: the Dashboard's loader starts everything the page reads, in
 * one wave, and returns at once. It reads one summary request and a few cheap
 * stored lists; it never asks a download client, indexer or disk anything.
 */
const api = vi.hoisted(() => ({
  fetchJson: vi.fn((_path: string) => new Promise<never>(() => {})),
  fetchPageItems: vi.fn((_path: string) => new Promise<never>(() => {}))
}));
vi.mock("../lib/api", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../lib/api")>()),
  fetchJson: api.fetchJson,
  fetchPageItems: api.fetchPageItems
}));

const { dashboardLoader } = await import("./dashboard-page");
const { queryClient } = await import("../lib/route-cache");

const asked = () => [...api.fetchJson.mock.calls, ...api.fetchPageItems.mock.calls].map(([path]) => path);
const request = (search: string) => ({ request: new Request(`http://deluno.local/${search}`) });

describe("dashboardLoader", () => {
  beforeEach(() => {
    queryClient.clear();
    api.fetchJson.mockClear();
    api.fetchPageItems.mockClear();
  });

  it("returns at once, even when every source is slow", () => {
    expect(dashboardLoader()).toEqual({ cached: true });
  });

  it("asks for the one summary and the setup lists, and nothing that reaches a client", () => {
    dashboardLoader(request(""));

    const paths = asked();
    expect(paths).toContain("/api/dashboard/summary");
    expect(paths).toContain("/api/libraries");
    expect(paths).toContain("/api/setup/progress");
    // The old page made twenty-four requests, including two live client reads.
    expect(paths.length).toBeLessThan(12);
    expect(paths).not.toContain("/api/download-clients/telemetry");
    expect(paths).not.toContain("/api/monitoring/dashboard");
  });

  it("starts the Library tab's own lists only when that tab is the one being opened", () => {
    dashboardLoader(request(""));
    expect(asked()).not.toContain("/api/movies/wanted");

    queryClient.clear();
    api.fetchJson.mockClear();
    dashboardLoader(request("?tab=library"));
    expect(asked()).toContain("/api/movies/wanted");
    expect(asked().some((path) => String(path).startsWith("/api/series/upcoming"))).toBe(true);
  });

  it("does not ask again for sources already on their way when the page mounts", () => {
    dashboardLoader();
    const first = api.fetchJson.mock.calls.length + api.fetchPageItems.mock.calls.length;
    dashboardLoader();
    expect(api.fetchJson.mock.calls.length + api.fetchPageItems.mock.calls.length).toBe(first);
  });
});
