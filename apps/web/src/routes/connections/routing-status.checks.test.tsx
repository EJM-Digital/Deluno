import { act, renderHook, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { LibraryRoutingSnapshot } from "../../lib/api";

const fetchJson = vi.hoisted(() => vi.fn());
vi.mock("../../lib/api", () => ({ fetchJson }));

const { clientProblemOf, routingStatus, useRoutingChecks } = await import("./routing-status");

const snapshot = (libraryId: string, overrides: Record<string, unknown> = {}) =>
  ({ libraryId, sources: [{ indexerId: "i1" }], downloadClients: [{ downloadClientId: "c1", category: "movies" }], ...overrides }) as unknown as LibraryRoutingSnapshot;
const report = (links: Array<{ status: string; message: string }>, clientName = "qBittorrent") => ({ clients: [{ clientName, links }] }) as never;

describe("the first thing wrong with a routed client", () => {
  it("names the client and the folder check's own words for the first problem", () => {
    expect(clientProblemOf(report([{ status: "ok", message: "fine" }, { status: "problem", message: "Category movies is missing." }]))).toEqual({ problem: "qBittorrent: Category movies is missing.", unknown: false });
  });

  it("carries an unknown link along with a problem found after it", () => {
    expect(clientProblemOf(report([{ status: "unknown", message: "?" }, { status: "problem", message: "Bad." }]))).toEqual({ problem: "qBittorrent: Bad.", unknown: true });
  });

  it("says there is no problem but something unknown when a link could not be checked", () => {
    expect(clientProblemOf(report([{ status: "unknown", message: "?" }, { status: "ok", message: "fine" }]))).toEqual({ problem: null, unknown: true });
  });

  it("says all is well when every link matches, or there are no clients", () => {
    expect(clientProblemOf(report([{ status: "ok", message: "fine" }]))).toEqual({ problem: null, unknown: false });
    expect(clientProblemOf({ clients: [] })).toEqual({ problem: null, unknown: false });
  });

  it("looks at every client, not only the first", () => {
    expect(clientProblemOf({ clients: [{ clientName: "A", links: [{ status: "ok", message: "" }] }, { clientName: "B", links: [{ status: "problem", message: "Off." }] }] } as never))
      .toEqual({ problem: "B: Off.", unknown: false });
  });
});

describe("the status of a library's routing", () => {
  it("asks for an indexer first, then a client, when either is missing", () => {
    expect(routingStatus(undefined)).toEqual({ tone: "warn", label: "No indexers" });
    expect(routingStatus(snapshot("l", { sources: [] }))).toEqual({ tone: "warn", label: "No indexers" });
    expect(routingStatus(snapshot("l", { downloadClients: [] }))).toEqual({ tone: "warn", label: "No client" });
  });

  it("says Needs a fix with the check's words when the folder check found a problem", () => {
    expect(routingStatus(snapshot("l"), { state: "done", problem: "qBittorrent: Off.", unknown: false })).toEqual({ tone: "warn", label: "Needs a fix", hint: "qBittorrent: Off.", needsFix: true });
  });

  it("says Checking while the check runs, even for a library that would be ready", () => {
    expect(routingStatus(snapshot("l"), { state: "checking" })).toEqual({ tone: "idle", label: "Checking" });
  });

  it("says Not fully checked when a client did not say where it saves", () => {
    expect(routingStatus(snapshot("l"), { state: "done", problem: null, unknown: true })).toEqual({
      tone: "info", label: "Not fully checked", hint: "A download client did not say where it saves. Open this library to check again."
    });
  });

  it("says Category route when a client has a category, and Ready when none does or the category is blank", () => {
    expect(routingStatus(snapshot("l"))).toEqual({ tone: "info", label: "Category route" });
    expect(routingStatus(snapshot("l", { downloadClients: [{ downloadClientId: "c1", category: "  " }] }))).toEqual({ tone: "ok", label: "Ready" });
    expect(routingStatus(snapshot("l", { downloadClients: [{ downloadClientId: "c1", category: null }] }), { state: "done", problem: null, unknown: false })).toEqual({ tone: "ok", label: "Ready" });
    expect(routingStatus(snapshot("l", { downloadClients: [{ downloadClientId: "c1", category: null }, { downloadClientId: "c2", category: "tv" }] }))).toEqual({ tone: "info", label: "Category route" });
  });
});

describe("the folder checks run in the background", () => {
  beforeEach(() => { fetchJson.mockReset(); });

  it("checks each library that has an indexer and a client, and reports what each found", async () => {
    fetchJson.mockImplementation(async (url: string) => (url.includes("/lib-a/") ? report([{ status: "problem", message: "Off." }]) : report([{ status: "unknown", message: "?" }])));
    const { result } = renderHook(() => useRoutingChecks(true, [{ id: "lib-a" }, { id: "lib-b" }, { id: "lib-c" }, { id: "lib-d" }], [snapshot("lib-a"), snapshot("lib-b"), snapshot("lib-c", { sources: [] }), snapshot("lib-d", { downloadClients: [] })]));

    await waitFor(() => expect(result.current["lib-a"]?.state).toBe("done"));
    await waitFor(() => expect(result.current["lib-b"]?.state).toBe("done"));
    expect(result.current["lib-a"]).toEqual({ state: "done", problem: "qBittorrent: Off.", unknown: false });
    expect(result.current["lib-b"]).toEqual({ state: "done", problem: null, unknown: true });
    expect(result.current["lib-c"]).toBeUndefined();
    expect(result.current["lib-d"]).toBeUndefined();
    expect(fetchJson).toHaveBeenCalledTimes(2);
    expect(fetchJson).toHaveBeenCalledWith("/api/libraries/lib-a/folder-chain", { signal: expect.any(AbortSignal) });
  });

  it("says Checking until the answer arrives", async () => {
    fetchJson.mockReturnValue(new Promise(() => undefined));
    const { result } = renderHook(() => useRoutingChecks(true, [{ id: "lib-a" }], [snapshot("lib-a")]));
    await waitFor(() => expect(result.current["lib-a"]).toEqual({ state: "checking" }));
  });

  it("says not fully checked when the check itself fails", async () => {
    fetchJson.mockRejectedValue(new Error("offline"));
    const { result } = renderHook(() => useRoutingChecks(true, [{ id: "lib-a" }], [snapshot("lib-a")]));
    await waitFor(() => expect(result.current["lib-a"]).toEqual({ state: "done", problem: null, unknown: true }));
  });

  it("runs nothing while switched off, or when no library is ready to check", () => {
    renderHook(() => useRoutingChecks(false, [{ id: "lib-a" }], [snapshot("lib-a")]));
    renderHook(() => useRoutingChecks(true, [{ id: "lib-a" }], []));
    renderHook(() => useRoutingChecks(true, [], [snapshot("lib-a")]));
    expect(fetchJson).not.toHaveBeenCalled();
  });

  it("checks again when a library's routed clients change, and stops the old check", async () => {
    let signal: AbortSignal | undefined;
    fetchJson.mockImplementation(async (_url: string, init: { signal: AbortSignal }) => { signal = init.signal; return report([]); });
    const libraries = [{ id: "lib-a" }];
    const { rerender, result } = renderHook(({ routing }) => useRoutingChecks(true, libraries, routing), { initialProps: { routing: [snapshot("lib-a")] } });
    await waitFor(() => expect(result.current["lib-a"]?.state).toBe("done"));
    const first = signal!;

    rerender({ routing: [snapshot("lib-a", { downloadClients: [{ downloadClientId: "c1", category: "tv" }] })] });

    await waitFor(() => expect(fetchJson).toHaveBeenCalledTimes(2));
    expect(first.aborted).toBe(true);
  });

  it("does not check again when the same routing is handed over again", async () => {
    fetchJson.mockResolvedValue(report([]));
    const libraries = [{ id: "lib-a" }];
    const { rerender, result } = renderHook(({ routing }) => useRoutingChecks(true, libraries, routing), { initialProps: { routing: [snapshot("lib-a")] } });
    await waitFor(() => expect(result.current["lib-a"]?.state).toBe("done"));
    rerender({ routing: [snapshot("lib-a")] });
    expect(fetchJson).toHaveBeenCalledTimes(1);
  });

  it("ignores an answer that arrives after the screen has gone", async () => {
    let answer: (value: unknown) => void = () => undefined;
    let fail: (reason: unknown) => void = () => undefined;
    fetchJson.mockReturnValueOnce(new Promise((resolve) => { answer = resolve; }));
    const first = renderHook(() => useRoutingChecks(true, [{ id: "lib-a" }], [snapshot("lib-a")]));
    first.unmount();
    answer(report([]));
    await Promise.resolve();
    expect(first.result.current["lib-a"]).toEqual({ state: "checking" });

    fetchJson.mockReturnValueOnce(new Promise((_resolve, reject) => { fail = reject; }));
    const second = renderHook(() => useRoutingChecks(true, [{ id: "lib-a" }], [snapshot("lib-a")]));
    second.unmount();
    fail(new Error("late"));
    await Promise.resolve();
    expect(second.result.current["lib-a"]).toEqual({ state: "checking" });
  });

  it("checks again when a client gains a category, and when a second client is added", async () => {
    fetchJson.mockResolvedValue(report([]));
    const libraries = [{ id: "lib-a" }];
    const bare = snapshot("lib-a", { downloadClients: [{ downloadClientId: "c1", category: null }] });
    const { rerender, result } = renderHook(({ routing }) => useRoutingChecks(true, libraries, routing), { initialProps: { routing: [bare] } });
    await waitFor(() => expect(result.current["lib-a"]?.state).toBe("done"));

    rerender({ routing: [snapshot("lib-a", { downloadClients: [{ downloadClientId: "c1", category: "tv" }] })] });
    await waitFor(() => expect(fetchJson).toHaveBeenCalledTimes(2));

    rerender({ routing: [snapshot("lib-a", { downloadClients: [{ downloadClientId: "c1", category: "tv" }, { downloadClientId: "c2", category: null }] })] });
    await waitFor(() => expect(fetchJson).toHaveBeenCalledTimes(3));
  });

  it("does not let an old answer overwrite the new check", async () => {
    let answerOld: (value: unknown) => void = () => undefined;
    fetchJson.mockReturnValueOnce(new Promise((resolve) => { answerOld = resolve; }));
    fetchJson.mockReturnValueOnce(new Promise(() => undefined));
    const libraries = [{ id: "lib-a" }];
    const { rerender, result } = renderHook(({ routing }) => useRoutingChecks(true, libraries, routing), { initialProps: { routing: [snapshot("lib-a")] } });
    await waitFor(() => expect(fetchJson).toHaveBeenCalledTimes(1));

    rerender({ routing: [snapshot("lib-a", { downloadClients: [{ downloadClientId: "c1", category: "tv" }] })] });
    await waitFor(() => expect(fetchJson).toHaveBeenCalledTimes(2));
    await act(async () => { answerOld(report([{ status: "problem", message: "Old." }])); await new Promise((r) => setTimeout(r, 10)); });

    expect(result.current["lib-a"]).toEqual({ state: "checking" });
  });

  it("does not let an old failure overwrite the new check", async () => {
    let failOld: (reason: unknown) => void = () => undefined;
    fetchJson.mockReturnValueOnce(new Promise((_resolve, reject) => { failOld = reject; }));
    fetchJson.mockReturnValueOnce(new Promise(() => undefined));
    const libraries = [{ id: "lib-a" }];
    const { rerender, result } = renderHook(({ routing }) => useRoutingChecks(true, libraries, routing), { initialProps: { routing: [snapshot("lib-a")] } });
    await waitFor(() => expect(fetchJson).toHaveBeenCalledTimes(1));
    rerender({ routing: [snapshot("lib-a", { downloadClients: [{ downloadClientId: "c1", category: "tv" }] })] });
    await waitFor(() => expect(fetchJson).toHaveBeenCalledTimes(2));
    await act(async () => { failOld(new Error("aborted")); await new Promise((r) => setTimeout(r, 10)); });

    expect(result.current["lib-a"]).toEqual({ state: "checking" });
  });
});
