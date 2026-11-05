import { renderHook, waitFor, act } from "@testing-library/react";
import { QueryClientProvider } from "@tanstack/react-query";
import type { ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";

const fetchJson = vi.fn();
vi.mock("../../../lib/api", async (original) => ({ ...(await original<object>()), fetchJson: (path: string) => fetchJson(path) }));

const handlers = new Map<string, (payload: unknown) => void>();
const subscriptions: [string, unknown][] = [];
vi.mock("../../../lib/use-signalr", async (original) => ({
  ...(await original<object>()),
  useLiveEvent: (name: string, handler: (payload: unknown) => void) => handlers.set(name, handler),
  useSignalREvent: (name: string, group: unknown, handler: (payload: unknown) => void) => {
    subscriptions.push([name, group]);
    handlers.set(name, handler);
  }
}));

import { DATABASES_KEY, LOG_KEY, OVERVIEW_KEY, systemSources, useSystemDatabases, useSystemLog, useSystemOverview } from "./data";
import { queryClient } from "../../../lib/route-cache";
import { RealtimeGroups } from "../../../lib/use-signalr";
import { liveSources } from "../sources";

/** What the System tab reads (#1098): the overview, today's log and the databases, each kept current by a push and never failing the screen. */
function wrapper({ children }: { children: ReactNode }) {
  return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
}
const entry = (id: string, level: "error" | "warning" | "info" = "info") => ({ id, timeUtc: "2026-10-01T10:00:00Z", level, message: id, source: null });

beforeEach(() => {
  queryClient.clear();
  fetchJson.mockReset();
  handlers.clear();
  subscriptions.length = 0;
});

describe("the System tab's stored lists", () => {
  it("reads the health checks, backups and backup settings from their own endpoints, and shares the Live reads", async () => {
    fetchJson.mockResolvedValue("body");
    await systemSources.healthChecks.fetch();
    await systemSources.backups.fetch();
    await systemSources.backupSettings.fetch();
    expect(fetchJson.mock.calls.map(([path]) => path)).toEqual(["/api/system/health", "/api/backups", "/api/backups/settings"]);
    expect(systemSources.backups.fallback).toEqual([]);
    expect(systemSources.healthChecks).toMatchObject({ key: ["src", "system-health"], label: "Health checks" });
    expect(systemSources.backups).toMatchObject({ key: ["src", "backups"], label: "Backups" });
    expect(systemSources.backupSettings).toMatchObject({ key: ["src", "backup-settings"], label: "Backup settings" });
    expect(systemSources.liveExtras).toBe(liveSources.liveExtras);
    expect(systemSources.latestActivity).toBe(liveSources.latestActivity);
    expect(systemSources.indexers).toBe(liveSources.indexers);
    expect(systemSources.clients).toBe(liveSources.clients);
  });
});

describe("the machine's overview", () => {
  it("is read once and kept", async () => {
    const overview = { instance: { version: "1" } };
    fetchJson.mockResolvedValue(overview);
    const { result } = renderHook(() => useSystemOverview(), { wrapper });
    expect(result.current).toBeNull();
    await waitFor(() => expect(result.current).toBe(overview));
    expect(fetchJson).toHaveBeenCalledWith("/api/system/overview");
  });

  it("is null for an older server's empty or wrong answer, or a failed read", async () => {
    for (const answer of [null, "text", {}]) {
      queryClient.clear();
      fetchJson.mockResolvedValueOnce(answer);
      const { result, unmount } = renderHook(() => useSystemOverview(), { wrapper });
      await waitFor(() => expect(queryClient.getQueryState(OVERVIEW_KEY)?.status).toBe("success"));
      expect(result.current).toBeNull();
      unmount();
    }
    queryClient.clear();
    fetchJson.mockRejectedValueOnce(new Error("down"));
    const { result } = renderHook(() => useSystemOverview(), { wrapper });
    await waitFor(() => expect(queryClient.getQueryState(OVERVIEW_KEY)?.status).toBe("error"));
    expect(result.current).toBeNull();
  });

  it("keeps the last answer on screen when a later read fails, instead of going blank (#1508)", async () => {
    const overview = { instance: { version: "1" } };
    fetchJson.mockResolvedValueOnce(overview);
    const { result } = renderHook(() => useSystemOverview(), { wrapper });
    await waitFor(() => expect(result.current).toBe(overview));
    fetchJson.mockRejectedValueOnce(new Error("down"));
    await act(async () => { await queryClient.refetchQueries({ queryKey: OVERVIEW_KEY }); });
    expect(queryClient.getQueryState(OVERVIEW_KEY)?.status).toBe("error");
    expect(result.current).toBe(overview);
  });

  it("takes the pushed overview, and ignores an empty or incomplete one", async () => {
    fetchJson.mockResolvedValue({ instance: { version: "1" } });
    const { result } = renderHook(() => useSystemOverview(), { wrapper });
    await waitFor(() => expect(result.current).not.toBeNull());
    const pushed = { instance: { version: "2" } };
    act(() => handlers.get("SystemOverviewChanged")!(pushed));
    expect(queryClient.getQueryData(OVERVIEW_KEY)).toEqual(pushed);
    await waitFor(() => expect(result.current).toEqual(pushed));
    act(() => handlers.get("SystemOverviewChanged")!(null));
    act(() => handlers.get("SystemOverviewChanged")!({}));
    expect(queryClient.getQueryData(OVERVIEW_KEY)).toEqual(pushed);
  });
});

describe("today's log", () => {
  const log = (...entries: ReturnType<typeof entry>[]) => ({ entries, errorsToday: 1, warningsToday: 2 });

  it("is read newest first, 200 at most, and kept", async () => {
    const body = log(entry("a"));
    fetchJson.mockResolvedValue(body);
    const { result } = renderHook(() => useSystemLog(), { wrapper });
    await waitFor(() => expect(result.current).toBe(body));
    expect(fetchJson).toHaveBeenCalledWith("/api/system/log?level=all&take=200");
  });

  it("is null for a server without it, a wrong answer or a failed read", async () => {
    for (const answer of [null, { entries: "x" }]) {
      queryClient.clear();
      fetchJson.mockResolvedValueOnce(answer);
      const { result, unmount } = renderHook(() => useSystemLog(), { wrapper });
      await waitFor(() => expect(queryClient.getQueryState(LOG_KEY)?.status).toBe("success"));
      expect(result.current).toBeNull();
      unmount();
    }
    queryClient.clear();
    fetchJson.mockRejectedValueOnce(new Error("down"));
    const { result } = renderHook(() => useSystemLog(), { wrapper });
    await waitFor(() => expect(queryClient.getQueryState(LOG_KEY)?.status).toBe("error"));
    expect(result.current).toBeNull();
  });

  it("keeps the last log on screen when a later read fails (#1508)", async () => {
    const body = log(entry("a"));
    fetchJson.mockResolvedValueOnce(body);
    const { result } = renderHook(() => useSystemLog(), { wrapper });
    await waitFor(() => expect(result.current).toBe(body));
    fetchJson.mockRejectedValueOnce(new Error("down"));
    await act(async () => { await queryClient.refetchQueries({ queryKey: LOG_KEY }); });
    expect(result.current).toBe(body);
  });

  it("puts a pushed entry at the top and counts errors and warnings", async () => {
    fetchJson.mockResolvedValue(log(entry("a")));
    const { result } = renderHook(() => useSystemLog(), { wrapper });
    await waitFor(() => expect(result.current).not.toBeNull());
    const push = handlers.get("SystemLogEntry")!;
    const cached = () => queryClient.getQueryData<ReturnType<typeof log>>(LOG_KEY)!;
    act(() => push(entry("e", "error")));
    expect(cached()).toMatchObject({ errorsToday: 2, warningsToday: 2 });
    act(() => push(entry("w", "warning")));
    expect(cached()).toMatchObject({ errorsToday: 2, warningsToday: 3 });
    act(() => push(entry("i", "info")));
    expect(cached()).toMatchObject({ errorsToday: 2, warningsToday: 3 });
    expect(cached().entries.map((row) => row.id)).toEqual(["i", "w", "e", "a"]);
    await waitFor(() => expect(result.current!.entries[0].id).toBe("i"));
  });

  it("ignores an entry already there, or one without an id, and keeps only the newest 200", async () => {
    fetchJson.mockResolvedValue(log(...Array.from({ length: 200 }, (_, i) => entry(`r${i}`))));
    const { result } = renderHook(() => useSystemLog(), { wrapper });
    await waitFor(() => expect(result.current).not.toBeNull());
    const push = handlers.get("SystemLogEntry")!;
    act(() => push(entry("r5", "error")));
    act(() => push(null));
    act(() => push({ level: "error" }));
    const cached = () => queryClient.getQueryData<ReturnType<typeof log>>(LOG_KEY)!;
    expect(cached().errorsToday).toBe(1);
    act(() => push(entry("new")));
    expect(cached().entries).toHaveLength(200);
    expect(cached().entries[0].id).toBe("new");
    expect(cached().entries[199].id).toBe("r198");
  });

  it("keeps nothing from a push when the log was never read", async () => {
    fetchJson.mockResolvedValue(null);
    const { result } = renderHook(() => useSystemLog(), { wrapper });
    await waitFor(() => expect(queryClient.getQueryState(LOG_KEY)?.status).toBe("success"));
    act(() => handlers.get("SystemLogEntry")!(entry("a")));
    expect(result.current).toBeNull();
    expect(queryClient.getQueryData(LOG_KEY)).toBeNull();
  });
});

describe("the databases", () => {
  it("are read once and shown", async () => {
    const body = { databases: [{ name: "main" }] };
    fetchJson.mockResolvedValue(body);
    const { result } = renderHook(() => useSystemDatabases(), { wrapper });
    expect(result.current).toEqual({ databases: null, failed: false });
    await waitFor(() => expect(result.current.databases).toBe(body));
    expect(result.current.failed).toBe(false);
    expect(fetchJson).toHaveBeenCalledWith("/api/system/databases");
  });

  it("show nothing, not failed, for an answer that is not a list", async () => {
    fetchJson.mockResolvedValue({ databases: "x" });
    const { result } = renderHook(() => useSystemDatabases(), { wrapper });
    await waitFor(() => expect(queryClient.getQueryState(DATABASES_KEY)?.status).toBe("success"));
    expect(result.current).toEqual({ databases: null, failed: false });
  });

  it("show nothing, not failed, for an empty answer", async () => {
    fetchJson.mockResolvedValue(null);
    const { result } = renderHook(() => useSystemDatabases(), { wrapper });
    await waitFor(() => expect(queryClient.getQueryState(DATABASES_KEY)?.status).toBe("success"));
    expect(result.current).toEqual({ databases: null, failed: false });
  });

  it("say the read failed when it did not go through (#1424)", async () => {
    fetchJson.mockRejectedValue(new Error("down"));
    const { result } = renderHook(() => useSystemDatabases(), { wrapper });
    await waitFor(() => expect(result.current.failed).toBe(true));
    expect(result.current.databases).toBeNull();
  });

  it("keep the last sizes on screen, and say the read failed, when a later read fails (#1508)", async () => {
    const body = { databases: [] };
    fetchJson.mockResolvedValueOnce(body);
    const { result } = renderHook(() => useSystemDatabases(), { wrapper });
    await waitFor(() => expect(result.current.databases).toBe(body));
    fetchJson.mockRejectedValueOnce(new Error("down"));
    await act(async () => { await queryClient.refetchQueries({ queryKey: DATABASES_KEY }); });
    await waitFor(() => expect(result.current).toEqual({ databases: body, failed: true }));
  });

  it("are read again when the server says they changed, not on a timer", async () => {
    fetchJson.mockResolvedValue({ databases: [] });
    renderHook(() => useSystemDatabases(), { wrapper });
    await waitFor(() => expect(fetchJson).toHaveBeenCalledTimes(1));
    expect(subscriptions).toContainEqual(["SystemDatabasesChanged", RealtimeGroups.Dashboard]);
    const invalidate = vi.spyOn(queryClient, "invalidateQueries");
    act(() => handlers.get("SystemDatabasesChanged")!(undefined));
    expect(invalidate).toHaveBeenCalledWith({ queryKey: DATABASES_KEY, exact: true });
    await waitFor(() => expect(fetchJson).toHaveBeenCalledTimes(2));
  });
});
