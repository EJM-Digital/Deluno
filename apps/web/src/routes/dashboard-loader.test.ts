import { beforeEach, describe, expect, it, vi } from "vitest";

/**
 * #1081: the Dashboard's loader starts everything the page reads, in one wave,
 * and returns at once. It used to wait for twenty-four requests in two serial
 * waves before the page could open.
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

describe("dashboardLoader (#1081)", () => {
  beforeEach(() => {
    queryClient.clear();
    api.fetchJson.mockClear();
    api.fetchPageItems.mockClear();
  });

  it("returns at once, even when every source is slow", () => {
    expect(dashboardLoader()).toEqual({ cached: true });
  });

  it("starts every source in the same breath, not in two waves", () => {
    dashboardLoader();

    const paths = [...api.fetchJson.mock.calls, ...api.fetchPageItems.mock.calls].map(([path]) => path);
    // The second wave used to wait for the first: the charts, the health board
    // and the activity ticker were among the requests that started late.
    expect(paths).toContain("/api/movies/wanted");
    expect(paths.some((path) => path.startsWith("/api/dashboard/metrics"))).toBe(true);
    expect(paths).toContain("/api/monitoring/dashboard");
    expect(paths.some((path) => path.startsWith("/api/activity"))).toBe(true);
    expect(paths.length).toBeGreaterThanOrEqual(24);
  });

  it("does not ask again for sources already on their way when the page mounts", () => {
    dashboardLoader();
    const first = api.fetchJson.mock.calls.length + api.fetchPageItems.mock.calls.length;
    dashboardLoader();
    expect(api.fetchJson.mock.calls.length + api.fetchPageItems.mock.calls.length).toBe(first);
  });
});
