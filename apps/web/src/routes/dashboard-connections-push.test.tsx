import { QueryClientProvider } from "@tanstack/react-query";
import { act, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { emptyPlatformSettingsSnapshot } from "../lib/api";
import { fullSummary } from "./dashboard/summary-fixtures";

/**
 * #1233: the Dashboard's connection list follows the health push, as System and the tray do. The processors come from the
 * Live extras, which the server remembers for five seconds, so the read after a push asks for a fresh copy.
 */
const handlers = vi.hoisted(() => new Map<string, Set<(payload: unknown) => void>>());
vi.mock("../lib/use-signalr", async (importOriginal) => {
  const { useEffect, useRef } = await import("react");
  const useListen = (name: string, handler: (payload: unknown) => void) => {
    const latest = useRef(handler);
    useEffect(() => { latest.current = handler; });
    useEffect(() => {
      const call = (payload: unknown) => latest.current(payload);
      const set = handlers.get(name) ?? new Set();
      set.add(call);
      handlers.set(name, set);
      return () => { set.delete(call); };
    }, [name]);
  };
  return {
    ...(await importOriginal<typeof import("../lib/use-signalr")>()),
    useSignalREvent: (name: string, _subjects: unknown, handler: (payload: unknown) => void) => useListen(name, handler),
    useSignalREvents: () => undefined,
    useSignalRResync: () => undefined,
    useLiveEvent: (name: string, handler: (payload: unknown) => void) => useListen(name, handler)
  };
});
vi.mock("../lib/use-auth", () => ({ authedFetch: vi.fn(() => Promise.resolve(new Response("{}"))) }));

const world = vi.hoisted(() => ({ weirHealthy: true, paths: [] as string[] }));
vi.mock("../lib/api", async (importOriginal) => {
  const original = await importOriginal<typeof import("../lib/api")>();
  const read = async (path: string) => {
    world.paths.push(path);
    if (path === "/api/dashboard/summary") return (await import("./dashboard/summary-fixtures")).fullSummary();
    if (path === "/api/settings") return original.emptyPlatformSettingsSnapshot;
    if (path === "/api/setup/progress") return { lastCompletedStep: 9, isSkipped: false, isCompleted: true, updatedUtc: "2026-09-30T00:00:00Z" };
    if (path.startsWith("/api/dashboard/live-extras")) {
      return {
        arrivals: [], importsToday: 0, protected: { refusedToday: 0 }, requests: [], retentionReclaimBytes: 0, turnedDown: [], cleanedUpToday: 0,
        connections: [{ kind: "processor", id: "p1", name: "Weir", enabled: true, healthy: world.weirHealthy, countToday: 0, busy: 0 }]
      };
    }
    if (path.startsWith("/api/dashboard/metrics")) return { days: 30, librarySize: [], titlesAdded: [] };
    if (path.endsWith("/wanted")) return { totalWanted: 0, missingCount: 0, upgradeCount: 0, coveredCount: 0, upcomingCount: 0, recentItems: [] };
    return [];
  };
  return { ...original, fetchJson: read, fetchAllPages: read, fetchPageItems: read };
});

const { DashboardPage } = await import("./dashboard-page");
const { queryClient } = await import("../lib/route-cache");

function send(name: string, payload: unknown = {}) {
  act(() => { for (const handler of handlers.get(name) ?? []) handler(payload); });
}

beforeEach(() => {
  queryClient.clear();
  handlers.clear();
  world.weirHealthy = true;
  world.paths = [];
});

describe("Dashboard connections follow the health push (#1233)", () => {
  it("shows the processor going down, and coming back, as soon as the health change is pushed", async () => {
    render(
      <QueryClientProvider client={queryClient}>
        <MemoryRouter initialEntries={["/?tab=system"]}>
          <DashboardPage />
        </MemoryRouter>
      </QueryClientProvider>
    );
    const connections = await screen.findByRole("region", { name: "Connections" });
    const weir = async () => (await within(connections).findByText("Weir")).closest("li, tr, [role=row], div")?.parentElement?.textContent ?? "";
    await waitFor(async () => expect(await weir()).toContain("answering"));
    expect(world.paths.some((path) => path.includes("fresh=true"))).toBe(false);

    world.weirHealthy = false;
    send("HealthChanged", { source: "processor", status: "offline", message: "Weir is not answering" });
    await waitFor(async () => expect(await weir()).toContain("not answering"), { timeout: 2_000 });
    // The read after the push goes past the server's five-second copy.
    expect(world.paths.some((path) => path.includes("live-extras") && path.includes("fresh=true"))).toBe(true);

    world.weirHealthy = true;
    send("HealthChanged", { source: "processor", status: "healthy", message: "" });
    await waitFor(async () => expect(await weir()).not.toContain("not answering"), { timeout: 2_000 });
  });
});
