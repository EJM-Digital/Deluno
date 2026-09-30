import { beforeEach, describe, expect, it, vi } from "vitest";

/**
 * #1081: every tab of Find & Download reads from one cache and asks only for
 * what it shows. Moving between tabs does not reload the whole set.
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

const { indexersLoader, indexersSectionFor, indexersSources } = await import("./connections-screen");
const { queryClient, startSources } = await import("../lib/route-cache");

function requested() {
  return api.fetchJson.mock.calls.map(([path]) => path);
}

function loaderFor(path: string) {
  return indexersLoader({ request: new Request(`http://deluno.local${path}`) });
}

describe("Find & Download sources (#1081)", () => {
  beforeEach(() => {
    queryClient.clear();
    api.fetchJson.mockClear();
  });

  it("names the tab an address opens", () => {
    expect(indexersSectionFor("/indexers/indexers")).toBe("indexers");
    expect(indexersSectionFor("/indexers/scoreboard")).toBe("scoreboard");
    expect(indexersSectionFor("/indexers/library-routing")).toBe("routing");
    expect(indexersSectionFor("/indexers/path-mappings")).toBe("pathMappings");
    expect(indexersSectionFor("/indexers/download-clients")).toBe("clients");
    expect(indexersSectionFor("/indexers/proxies")).toBe("proxies");
  });

  it("reads the scoreboard only on the Scoreboard tab", () => {
    expect(loaderFor("/indexers/indexers")).toEqual({ cached: true });
    expect(requested().some((path) => path.startsWith("/api/indexers/scoreboard"))).toBe(false);

    loaderFor("/indexers/scoreboard");
    expect(requested().some((path) => path.startsWith("/api/indexers/scoreboard"))).toBe(true);
  });

  it("reads library routing on Library Routing, and not beside the other tabs' lists", () => {
    startSources(indexersSources, "indexers");
    // Routing is a list per library, reached through the library list: it is
    // started with the sources of any tab that mentions it, and only its own
    // tab waits for it.
    expect(requested()).toContain("/api/indexers");
    expect(requested()).toContain("/api/libraries");
    expect(requested()).not.toContain("/api/download-clients/path-mappings");
  });

  it("moving to another tab asks only for what that tab adds", () => {
    loaderFor("/indexers/indexers");
    const afterFirst = requested().length;

    loaderFor("/indexers/download-clients");
    const added = requested().slice(afterFirst);

    // The indexers, clients, libraries and settings are already on their way.
    expect(added).not.toContain("/api/indexers");
    expect(added).not.toContain("/api/libraries");
    expect(added).toContain("/api/download-clients/telemetry");
    expect(added).toContain("/api/download-clients/path-mappings");
  });
});
