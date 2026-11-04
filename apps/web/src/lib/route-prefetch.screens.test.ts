import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

/** Which screen's code and queries each address starts (#1081), and the idle fetch of the main screens. */
const hoisted = vi.hoisted(() => ({
  imported: [] as string[],
  started: [] as string[],
  fail: { current: false }
}));

function screen(name: string, loaders: Record<string, string> = {}) {
  hoisted.imported.push(name);
  if (hoisted.fail.current) throw new Error("chunk failed");
  const module: Record<string, unknown> = { default: name };
  for (const [exportName, label] of Object.entries(loaders)) module[exportName] = () => { hoisted.started.push(label); };
  return module;
}

const SCREENS: Array<[string, string, Record<string, string>?]> = [
  ["dashboard-page", "dashboard", { dashboardLoader: "dashboard" }],
  ["library-page", "library", { moviesLoader: "movies", showsLoader: "shows" }],
  ["queue-page", "queue", { queueLoader: "queue" }],
  ["connections-screen", "connections", { indexersLoader: "indexers" }],
  ["system-page", "system", { systemLoader: "system" }],
  ["calendar-page", "calendar", { calendarLoader: "calendar" }],
  ["collections-page", "collections"],
  ["discover-page", "discover"],
  ["search-page", "search"],
  ["search-cycles-page", "search-cycles"],
  ["settings-overview-page", "settings"]
];

async function load() {
  vi.resetModules();
  for (const [file, name, loaders] of SCREENS) vi.doMock("../routes/" + file, () => screen(name, loaders));
  return import("./route-prefetch");
}

beforeEach(() => { hoisted.imported.length = 0; hoisted.started.length = 0; hoisted.fail.current = false; });
afterEach(() => { vi.useRealTimers(); });

describe("which screen an address warms up", () => {
  it.each([
    ["/", "dashboard", "dashboard"],
    ["/movies", "library", "movies"],
    ["/tv", "library", "shows"],
    ["/queue", "queue", "queue"],
    ["/indexers", "connections", "indexers"],
    ["/indexers/indexers", "connections", "indexers"],
    ["/indexers/download-clients", "connections", "indexers"],
    ["/system", "system", "system"],
    ["/system/updates", "system", "system"],
    ["/system/updates/api", "system", "system"],
    ["/system/x/docs", "system", "system"],
    ["/calendar", "calendar", "calendar"]
  ])("%s fetches the %s screen and starts its %s queries", async (path, screenName, loader) => {
    const { prefetchRoute } = await load();
    prefetchRoute(path, { data: true });
    await vi.waitFor(() => expect(hoisted.started).toEqual([loader]));
    expect(hoisted.imported).toEqual([screenName]);
  });

  it.each([
    ["/collections", "collections"], ["/discover", "discover"], ["/search", "search"], ["/search-cycles", "search-cycles"], ["/search-cycles/history", "search-cycles"],
    ["/settings", "settings"], ["/settings/libraries", "settings"]
  ])("%s fetches only the %s screen's code, having no queries to start", async (path, screenName) => {
    const { prefetchRoute } = await load();
    prefetchRoute(path, { data: true });
    await vi.waitFor(() => expect(hoisted.imported).toEqual([screenName]));
    expect(hoisted.started).toEqual([]);
  });

  it("ignores the query, the fragment and a trailing slash of an address", async () => {
    const { prefetchRoute } = await load();
    prefetchRoute("/movies/?sort=title#top", { data: true });
    await vi.waitFor(() => expect(hoisted.started).toEqual(["movies"]));
    prefetchRoute("/movies//", { data: true });
    await vi.waitFor(() => expect(hoisted.started).toEqual(["movies", "movies"]));
    hoisted.started.length = 1;
    prefetchRoute("/?x=1", { data: true });
    await vi.waitFor(() => expect(hoisted.started).toEqual(["movies", "dashboard"]));
  });

  it.each(["/indexers/history", "/indexers/history/x", "/indexers/subtitle/x", "/indexers/media-servers/x", "/indexers/companion/x", "/indexers/subtitle", "/indexers/media-servers", "/indexers/companion", "/system/api", "/system/docs", "/system/security", "/moviesx", "/tvx", "/queuex", "/calendars", "/nowhere"])("%s warms nothing", async (path) => {
    const { prefetchRoute } = await load();
    prefetchRoute(path, { data: true });
    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(hoisted.imported).toEqual([]);
  });

  it("never throws when the chunk cannot be fetched", async () => {
    hoisted.fail.current = true;
    const { prefetchRoute } = await load();
    expect(() => prefetchRoute("/queue", { data: true })).not.toThrow();
    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(hoisted.started).toEqual([]);
  });
});

describe("the main screens fetched when the app is idle", () => {
  it("fetches one at a time, a timer apart, and only once however often it is asked", async () => {
    vi.useFakeTimers();
    const { prefetchMainRoutesWhenIdle } = await load();
    prefetchMainRoutesWhenIdle();
    prefetchMainRoutesWhenIdle();
    expect(hoisted.imported).toEqual([]);
    expect(vi.getTimerCount()).toBe(1);

    const order: string[][] = [];
    for (let step = 0; step < 8; step += 1) {
      await vi.advanceTimersByTimeAsync(1_200);
      order.push([...hoisted.imported]);
    }
    expect(order[0]).toEqual(["dashboard"]);
    expect(order[1]).toEqual(["dashboard", "library"]);
    expect(hoisted.imported.filter((name) => name === "library")).toHaveLength(1);
    expect(hoisted.imported).toEqual(["dashboard", "library", "queue", "connections", "system", "calendar"]);
    expect(hoisted.started).toEqual([]);
  });

  it("waits for the browser's own idle time when it has one", async () => {
    const calls: Array<{ callback: () => void; options: unknown }> = [];
    (window as unknown as { requestIdleCallback: unknown }).requestIdleCallback = (callback: () => void, options: unknown) => { calls.push({ callback, options }); return 1; };
    try {
      const { prefetchMainRoutesWhenIdle } = await load();
      prefetchMainRoutesWhenIdle();
      expect(calls).toHaveLength(1);
      expect(calls[0].options).toEqual({ timeout: 3_000 });
      calls[0].callback();
      await vi.waitFor(() => expect(hoisted.imported).toEqual(["dashboard"]));
      expect(calls).toHaveLength(2);
    } finally {
      delete (window as unknown as { requestIdleCallback?: unknown }).requestIdleCallback;
    }
  });

  it("does nothing where there is no window", async () => {
    const { prefetchMainRoutesWhenIdle } = await load();
    vi.stubGlobal("window", undefined);
    try {
      expect(() => prefetchMainRoutesWhenIdle()).not.toThrow();
    } finally {
      vi.unstubAllGlobals();
    }
    expect(hoisted.imported).toEqual([]);
  });
});
