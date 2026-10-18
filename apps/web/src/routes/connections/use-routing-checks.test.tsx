import { act, renderHook, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { LibraryRoutingSnapshot } from "../../lib/api";
import type { FolderChainReport } from "./folder-chain-section";

const api = vi.hoisted(() => ({ fetchJson: vi.fn() }));
vi.mock("../../lib/api", async (importOriginal) => ({ ...(await importOriginal<typeof import("../../lib/api")>()), fetchJson: api.fetchJson }));

const { useRoutingChecks } = await import("./routing-status");

const link = (clientId: string, category: string | null = null) => ({ id: clientId, libraryId: "", downloadClientId: clientId, downloadClientName: clientId, priority: 1, category, createdUtc: "", updatedUtc: "" });
const source = { id: "s", libraryId: "", indexerId: "i", indexerName: "Indexer", priority: 1, requiredTags: "", excludedTags: "", createdUtc: "", updatedUtc: "" };
const snapshot = (libraryId: string, clients: ReturnType<typeof link>[], sources = [source]): LibraryRoutingSnapshot => ({ libraryId, libraryName: libraryId, sources, downloadClients: clients });
const report = (status: "ok" | "problem" | "unknown", message = ""): FolderChainReport => ({ clients: [{ clientName: "Deluge", links: [{ status, message }] }] } as unknown as FolderChainReport);

/** #1133: Library Routing asks the folder check for each library that has an indexer and a client, in the background. */
describe("the folder check Library Routing runs for every library", () => {
  beforeEach(() => { api.fetchJson.mockReset(); });

  it("asks for each library that has an indexer and a client, and only for those, and reads a problem in the check's own words", async () => {
    api.fetchJson.mockImplementation(async (path: string) => (path === "/api/libraries/movies/folder-chain" ? report("problem", "no category.") : report("ok")));
    const libraries = [{ id: "movies" }, { id: "shows" }, { id: "music" }, { id: "docs" }];
    const routing = [snapshot("movies", [link("d")]), snapshot("shows", [link("q")]), snapshot("music", [], [source]), snapshot("docs", [link("q")], [])];

    const { result } = renderHook(() => useRoutingChecks(true, libraries, routing));

    await waitFor(() => expect(result.current.movies?.state).toBe("done"));
    await waitFor(() => expect(result.current.shows?.state).toBe("done"));
    expect(result.current.movies).toEqual({ state: "done", problem: "Deluge: no category.", unknown: false });
    expect(result.current.shows).toEqual({ state: "done", problem: null, unknown: false });
    expect(api.fetchJson.mock.calls.map(([path]) => path).sort()).toEqual(["/api/libraries/movies/folder-chain", "/api/libraries/shows/folder-chain"]);
    expect(result.current.music).toBeUndefined();
    expect(result.current.docs).toBeUndefined();
  });

  it("reads a check that failed, or that a client could not answer, as not fully checked rather than ready", async () => {
    api.fetchJson.mockImplementation(async (path: string) => {
      if (path === "/api/libraries/movies/folder-chain") throw new Error("the server did not answer");
      return report("unknown");
    });
    const { result } = renderHook(() => useRoutingChecks(true, [{ id: "movies" }, { id: "shows" }], [snapshot("movies", [link("d")]), snapshot("shows", [link("q")])]));

    await waitFor(() => expect(result.current.movies?.state).toBe("done"));
    await waitFor(() => expect(result.current.shows?.state).toBe("done"));
    expect(result.current.movies).toEqual({ state: "done", problem: null, unknown: true });
    expect(result.current.shows).toEqual({ state: "done", problem: null, unknown: true });
  });

  it("shows a library as checking while the answer is on its way, and asks nothing when the tab is not open", async () => {
    let settle: (value: FolderChainReport) => void = () => undefined;
    api.fetchJson.mockImplementation(() => new Promise<FolderChainReport>((resolve) => { settle = resolve; }));
    const closed = renderHook(() => useRoutingChecks(false, [{ id: "movies" }], [snapshot("movies", [link("d")])]));
    expect(api.fetchJson).not.toHaveBeenCalled();
    expect(closed.result.current).toEqual({});

    const open = renderHook(() => useRoutingChecks(true, [{ id: "movies" }], [snapshot("movies", [link("d")])]));
    await waitFor(() => expect(open.result.current.movies).toEqual({ state: "checking" }));
    await act(async () => settle(report("ok")));
    await waitFor(() => expect(open.result.current.movies?.state).toBe("done"));
  });

  it("runs the check again for a library when its routed clients change, and not when nothing changed", async () => {
    api.fetchJson.mockResolvedValue(report("ok"));
    const libraries = [{ id: "movies" }];
    const { rerender, result } = renderHook(({ routing }) => useRoutingChecks(true, libraries, routing), { initialProps: { routing: [snapshot("movies", [link("d")])] } });
    await waitFor(() => expect(result.current.movies?.state).toBe("done"));
    expect(api.fetchJson).toHaveBeenCalledTimes(1);

    rerender({ routing: [snapshot("movies", [link("d")])] });
    expect(api.fetchJson).toHaveBeenCalledTimes(1);

    rerender({ routing: [snapshot("movies", [link("d"), link("q", "movies")])] });
    await waitFor(() => expect(api.fetchJson).toHaveBeenCalledTimes(2));
  });
});
